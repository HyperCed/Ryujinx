using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nx2
{
    // The Switch 2 video decoder (nn::codec::video::detail::ipc), port of Lichii's INx2VideoSession.
    // gsv 902/905/906/1002 hand out a session whose TIPC 1 (CreateVideoDecoderService) creates a decoder
    // service, another session of this class. Request words below are data words (Lichii's w[n + 2]):
    // - 200/201/202/210: initialize; 6 / 207 / 8: start / stop / flush; 115: finalize.
    // - 101(A or X: codec name, "video/vp9"; u32 x2, u32 width, u32 height, u32 rate): configure.
    // - 3(): the submission-ready event.
    // - 311(u32 image id): register an output image (gsv 911 creates it); reply its NativeSyncHandle
    //   {~syncpoint, 0, u64 value}, reached once a frame has been decoded into the image. The client
    //   acquires images in registration/release order, so frames fill them in that order.
    // - 314(u32 image id, u32, NativeSyncHandle release): give an image back; reply its next handle.
    // - 212(u32 image id): unregister it.
    // - 109/400(A: one compressed frame, u64 pts in us, u8 end of stream): submit input; reply a u64 id,
    //   or busy (nn::codec 3603) while the queue is full.
    // - 213(): the next finished frame (16 words: image, width, height, width, height, 0, pts ns, end).
    // Each decoder service owns a syncpoint counting its finished frames. FFmpeg decodes on a thread of
    // the session's own into the pitch-linear layout gsv 961 describes.
    class INx2VideoSession : IpcService, INx2StubService, IDisposable
    {
        private const ulong MarioKartWorldProgramId = 0x0400C3F00006E000UL;
        private const int MaxDecodedAhead = 2;
        private const int RefuseAt = 24;
        private const uint NoSyncpoint = uint.MaxValue;
        private const uint ResultEmpty = 0x1c286f;
        private const uint ResultBusy = 111 | (3603 << 9); // nn::codec (module 111) 3603

        private static readonly bool _trace = Environment.GetEnvironmentVariable("RYUJINX_NX2_VDEC_TRACE") == "1";
        private static readonly bool _noDecode = Environment.GetEnvironmentVariable("RYUJINX_NX2_VDEC_NO_DECODE") == "1";
        private static readonly string _eosDrain = Environment.GetEnvironmentVariable("RYUJINX_NX2_VDEC_EOS_DRAIN");

        private record struct Input(byte[] Data, ulong Pts, bool End);
        private record struct Decoded(Nx2Picture Picture, ulong Pts, bool End);
        private record struct Output(uint Image, uint Width, uint Height, ulong Pts, bool End);
        private record struct Fence(uint Syncpoint, uint Value);

        private readonly Nx2NativeObjects _objects;

        private KEvent _event;
        private Timer _readyTimer;
        private volatile bool _running = true;
        private volatile bool _room = true;

        private Switch _device;
        private IVirtualMemoryManager _memory;
        private ulong _programId;
        private ulong _submitted;
        private int _writeFailures;
        private bool _untrackedWrites;
        private uint _syncpoint = NoSyncpoint;
        private uint _syncBase;

        // Shared with the decode thread, under _lock.
        private readonly object _lock = new();
        private string _codec = "";
        private uint _width;
        private uint _height;
        private uint _handed;
        private ulong _framesOut;
        private readonly Queue<Input> _inputs = new();
        private Input? _held;
        private readonly LinkedList<Decoded> _decoded = new();
        private readonly LinkedList<uint> _free = new();
        private readonly Dictionary<uint, Fence> _releaseFences = [];
        private readonly Queue<Output> _outputs = new();
        private readonly HashSet<uint> _registered = [];
        private Output _lastOutput;
        private bool _streamEnded;
        private bool _endQueued;
        private bool _flush;
        private bool _quit;
        private Thread _worker;

        public string InterfaceName { get; }

        public bool IsZeroedCommand(int commandId) => true;

        // The client's memory: context.Memory is the HLE server's own, where request buffers are copied.
        private readonly Func<IVirtualMemoryManager> _clientMemory;

        public INx2VideoSession(string name, Nx2NativeObjects objects, Func<IVirtualMemoryManager> clientMemory) : base(registerTipc: true)
        {
            InterfaceName = name;
            _objects = objects;
            _clientMemory = clientMemory;
        }

        private void Trace(string message)
        {
            if (_trace)
            {
                Logger.Info?.Print(LogClass.ServiceNv, $"{InterfaceName} {message}");
            }
        }

        private void Bind(ServiceCtx context)
        {
            _device ??= context.Device;
            _memory ??= _clientMemory();

            if (_programId == 0)
            {
                _programId = context.Device.Processes.ActiveApplication?.ProgramId ?? 0;
            }
        }

        private SynchronizationManager Sync => _device.Gpu.Synchronization;

        [CommandTipc(1)]
        // CreateVideoDecoderService: a decoder service session.
        public ResultCode CreateDecoderService(ServiceCtx context)
        {
            Logger.Info?.Print(LogClass.ServiceNv, $"{InterfaceName} 1: creating a video decoder service");

            return Nx2Tipc.ReturnSession(context, this, new INx2VideoSession(InterfaceName + ":decoder", _objects, _clientMemory));
        }

        [CommandTipc(200)]
        [CommandTipc(201)]
        [CommandTipc(202)]
        [CommandTipc(210)]
        // 200/201/202 copy the work buffer's transfer memory handle. Holding it kept the memory locked after
        // the game freed it: reused for a file read, nn::fs::ReadFile failed with InvalidCurrentMemory and the
        // movie player aborted.
        public ResultCode Initialize(ServiceCtx context)
        {
            Bind(context);

            if (context.Request.HandleDesc != null)
            {
                foreach (int handle in context.Request.HandleDesc.ToCopy)
                {
                    context.Device.System.KernelContext.Syscall.CloseHandle(handle);
                }
            }

            return ResultCode.Success;
        }

        [CommandTipc(3)]
        // GetEvent: the submission-ready event. The client attaches it auto-clearing, so one signal is
        // consumed by the first wait: while the input queue has room, re-signal every 4 ms.
        public ResultCode GetEvent(ServiceCtx context)
        {
            Bind(context);

            if (_event == null)
            {
                _event = new KEvent(context.Device.System.KernelContext);
                _readyTimer = new Timer(_ => SignalReady(), null, 4, 4);
            }

            SignalReady();

            return Nx2Tipc.ReturnEvent(context, _event);
        }

        private void SignalReady()
        {
            KEvent evt = _event;

            if (evt != null && _running && _room)
            {
                evt.WritableEvent.Signal();
            }
        }

        private void SetRunning(bool running)
        {
            _running = running;

            if (!running)
            {
                _event?.WritableEvent.Clear();
            }
        }

        [CommandTipc(6)]
        public ResultCode Start(ServiceCtx context)
        {
            Bind(context);
            Trace($"start syncpoint={_syncpoint}");
            SetRunning(true);
            SignalReady();

            return ResultCode.Success;
        }

        [CommandTipc(101)]
        public ResultCode Configure(ServiceCtx context)
        {
            Bind(context);

            // Lichii reads width, height and rate at command words 5-7, after the codec name's buffer
            // descriptor (3 words for A, 2 for X); data words start after the descriptors.
            uint[] w = Nx2Tipc.Words(context);
            int first = Math.Max(3 - 3 * context.Request.SendBuff.Count - 2 * context.Request.PtrBuff.Count, 0);
            string codec = ReadCodecName(context);

            Logger.Info?.Print(LogClass.ServiceNv, $"{InterfaceName} configure codec \"{codec}\" {w[first]}x{w[first + 1]} rate {w[first + 2]}");

            lock (_lock)
            {
                _codec = codec;
                _width = w[first];
                _height = w[first + 1];
            }

            if (_worker == null)
            {
                _worker = new Thread(DecodeLoop) { Name = "Nx2.VideoDecoder", IsBackground = true };
                _worker.Start();
            }

            return ResultCode.Success;
        }

        private static string ReadCodecName(ServiceCtx context)
        {
            byte[] name = null;

            if (context.Request.SendBuff.Count > 0 && context.Request.SendBuff[0].Size != 0)
            {
                name = new byte[context.Request.SendBuff[0].Size];
                context.Memory.Read(context.Request.SendBuff[0].Position, name);
            }
            else if (context.Request.PtrBuff.Count > 0 && context.Request.PtrBuff[0].Size != 0)
            {
                name = new byte[context.Request.PtrBuff[0].Size];
                context.Memory.Read(context.Request.PtrBuff[0].Position, name);
            }

            if (name == null)
            {
                return "";
            }

            int end = Array.IndexOf(name, (byte)0);
            string codec = System.Text.Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end);

            if (codec.Length == 0)
            {
                string all = System.Text.Encoding.ASCII.GetString(name);

                foreach (string candidate in new[] { "video/avc", "video/vp9", "video/vp8", "video/hevc" })
                {
                    if (all.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return codec;
        }

        // Queues an output image for the next decoded frame; returns the NativeSyncHandle that signals
        // once that frame is in it.
        private void HandOut(ServiceCtx context, uint image, Fence? releaseFence)
        {
            Bind(context);

            if (_syncpoint == NoSyncpoint)
            {
                _syncpoint = context.Device.System.HostSyncpoint.AllocateSyncpoint(false);
                _syncBase = Sync.GetSyncpointValue(_syncpoint);
                Logger.Info?.Print(LogClass.ServiceNv, $"{InterfaceName} counts decoded frames on syncpoint {_syncpoint} from {_syncBase}");
            }

            uint value;

            lock (_lock)
            {
                _free.AddLast(image);
                _registered.Add(image);

                if (releaseFence.HasValue)
                {
                    _releaseFences[image] = releaseFence.Value;
                }

                // Mario Kart World waits for returned images after a movie and acquires a frame for each
                // completed fence, so its released images need synthetic end-of-stream frames.
                if (DrainAtEnd() && releaseFence.HasValue && _streamEnded && _inputs.Count == 0 && _decoded.Count == 0)
                {
                    uint reached = DrainFreeAsEndOfStream();
                    _syncBase = reached;
                    _handed = 0;
                    value = reached;
                }
                else
                {
                    value = _syncBase + ++_handed;
                }

                Trace($"handout syncpoint={_syncpoint} image={image} release={releaseFence.HasValue} fence={value} current={Sync.GetSyncpointValue(_syncpoint)} eos={_streamEnded} inputs={_inputs.Count} decoded={_decoded.Count} outputs={_outputs.Count}");

                Monitor.PulseAll(_lock);
            }

            Nx2Tipc.Reply(context, ~_syncpoint, 0, value, 0);
        }

        private bool DrainAtEnd() => _eosDrain switch
        {
            "1" => true,
            "0" => false,
            _ => _programId == MarioKartWorldProgramId,
        };

        [CommandTipc(311)]
        public ResultCode RegisterBuffer(ServiceCtx context)
        {
            HandOut(context, Nx2Tipc.Words(context)[0], null);

            return ResultCode.Success;
        }

        [CommandTipc(314)]
        public ResultCode ReleaseBuffer(ServiceCtx context)
        {
            uint[] w = Nx2Tipc.Words(context);

            HandOut(context, w[0], new Fence(~w[2], w[4]));

            return ResultCode.Success;
        }

        [CommandTipc(212)]
        public ResultCode UnregisterBuffer(ServiceCtx context)
        {
            uint image = Nx2Tipc.Words(context)[0];

            lock (_lock)
            {
                _free.Remove(image);
                _registered.Remove(image);
                _releaseFences.Remove(image);
            }

            return ResultCode.Success;
        }

        [CommandTipc(109)]
        [CommandTipc(400)]
        public ResultCode SubmitInput(ServiceCtx context)
        {
            Bind(context);

            uint[] w = Nx2Tipc.Words(context);
            ulong pts = Nx2Tipc.Pair(w, 0);
            bool end = (w[2] & 0xff) != 0;
            byte[] data = [];

            if (context.Request.SendBuff.Count > 0 && context.Request.SendBuff[0].Size != 0)
            {
                data = new byte[context.Request.SendBuff[0].Size];
                context.Memory.Read(context.Request.SendBuff[0].Position, data);
            }

            Input input = new(data, pts, end);

            lock (_lock)
            {
                // A refused packet is held rather than lost: a client that sends it again gets it
                // deduplicated, one that moves on gets it queued ahead of the next.
                if (_held is Input held)
                {
                    if (held.Pts != input.Pts || !held.Data.AsSpan().SequenceEqual(input.Data))
                    {
                        _inputs.Enqueue(held);
                    }

                    _held = null;
                }

                if (!input.End && _inputs.Count >= RefuseAt)
                {
                    _held = input;
                    _room = false;
                    Trace($"refuse syncpoint={_syncpoint} pts={pts} queued={_inputs.Count}");
                    return (ResultCode)ResultBusy;
                }

                _inputs.Enqueue(input);
                _streamEnded = false;
                _endQueued |= end;
                _room = !_endQueued && _inputs.Count < RefuseAt;
                Trace($"submit syncpoint={_syncpoint} input={_submitted + 1} bytes={data.Length} pts={pts} eos={end} queued={_inputs.Count}");

                Monitor.PulseAll(_lock);
            }

            ulong id = ++_submitted;
            Nx2Tipc.Reply64(context, id);

            return ResultCode.Success;
        }

        [CommandTipc(8)]
        [CommandTipc(207)]
        // Flush (8) and Stop (207): drop queued input and frames not yet in an image. Images already
        // handed out keep their place, so the sync values stay in step with the client's ring.
        public ResultCode Flush(ServiceCtx context)
        {
            Bind(context);

            bool stop = (int)context.Request.Type - 0x10 == 207;

            if (stop)
            {
                SetRunning(false);
            }

            lock (_lock)
            {
                Trace($"{(stop ? "stop" : "flush")} syncpoint={_syncpoint} inputs={_inputs.Count} decoded={_decoded.Count} outputs={_outputs.Count} frames_out={_framesOut} eos={_streamEnded}");

                _inputs.Clear();
                _held = null;
                _flush = true;
                _streamEnded = false;
                _endQueued = false;
                _room = true;

                if (stop)
                {
                    CompleteHandedFences();
                }

                Monitor.PulseAll(_lock);
            }

            return ResultCode.Success;
        }

        [CommandTipc(115)]
        public ResultCode Finalize(ServiceCtx context)
        {
            Bind(context);
            SetRunning(false);

            lock (_lock)
            {
                Trace($"finalize syncpoint={_syncpoint} inputs={_inputs.Count} decoded={_decoded.Count} outputs={_outputs.Count} frames_out={_framesOut}");
                CompleteHandedFences();
            }

            return ResultCode.Success;
        }

        // Advances the syncpoint to the last value handed out (_lock held).
        private void CompleteHandedFences()
        {
            if (_syncpoint == NoSyncpoint)
            {
                return;
            }

            uint target = _syncBase + _handed;
            uint current = Sync.GetSyncpointValue(_syncpoint);

            Trace($"complete fences syncpoint={_syncpoint} current={current} target={target}");

            while ((int)(target - current) > 0)
            {
                Sync.IncrementSyncpoint(_syncpoint);
                current++;
            }
        }

        [CommandTipc(213)]
        public ResultCode GetOutputInfo(ServiceCtx context)
        {
            Output output;

            lock (_lock)
            {
                if (!_outputs.TryDequeue(out output))
                {
                    // Nothing finished: the code the client's own Acquire uses for an empty ring.
                    return (ResultCode)ResultEmpty;
                }
            }

            ulong ptsNs = output.Pts * 1000;

            Trace($"output syncpoint={_syncpoint} image={output.Image} size={output.Width}x{output.Height} pts={output.Pts} eos={output.End}");
            Nx2Tipc.Reply(context, output.Image, output.Width, output.Height, output.Width, output.Height, 0,
                (uint)ptsNs, (uint)(ptsNs >> 32), output.End ? 1u : 0u, 0, 0, 0, 0, 0, 0, 0);

            return ResultCode.Success;
        }

        // Synthetic end-of-stream frames in every free image (_lock held); returns the syncpoint value.
        private uint DrainFreeAsEndOfStream()
        {
            Trace($"synthetic EOS syncpoint={_syncpoint} images={_free.Count}");

            while (_free.Count > 0)
            {
                uint image = _free.First.Value;
                _free.RemoveFirst();
                _releaseFences.Remove(image);
                _outputs.Enqueue(new Output(image, _lastOutput.Width, _lastOutput.Height, _lastOutput.Pts, true));
                Sync.IncrementSyncpoint(_syncpoint);
            }

            return Sync.GetSyncpointValue(_syncpoint);
        }

        // The decode thread: turns queued input into decoded frames and decoded frames into output
        // images, in order.
        private void DecodeLoop()
        {
            Nx2VideoDecoder decoder = null;
            List<Nx2Picture> pictures = [];
            byte[] bytes = [];

            try
            {
                lock (_lock)
                {
                    while (true)
                    {
                        while (!(_quit || _flush || (_decoded.Count > 0 && _free.Count > 0) ||
                                 (_inputs.Count > 0 && _decoded.Count < MaxDecodedAhead)))
                        {
                            Monitor.Wait(_lock);
                        }

                        if (_quit)
                        {
                            return;
                        }

                        if (_flush)
                        {
                            _flush = false;
                            _decoded.Clear();
                            decoder?.Reset();

                            continue;
                        }

                        if (_decoded.Count > 0 && _free.Count > 0)
                        {
                            Decoded frame = _decoded.First.Value;
                            _decoded.RemoveFirst();
                            uint image = _free.First.Value;
                            _free.RemoveFirst();
                            Fence? fence = null;

                            if (_releaseFences.Remove(image, out Fence f))
                            {
                                fence = f;
                            }

                            Monitor.Exit(_lock);

                            (uint width, uint height) size;

                            try
                            {
                                if (fence.HasValue)
                                {
                                    WaitForRelease(fence.Value);
                                }

                                size = WriteImage(image, frame, ref bytes);
                            }
                            finally
                            {
                                Monitor.Enter(_lock);
                            }

                            // Even an image written during a flush counts: the client waits for its value.
                            Output output = new(image, size.width, size.height, frame.Pts, frame.End);
                            _outputs.Enqueue(output);
                            _lastOutput = output;

                            if (frame.End)
                            {
                                _streamEnded = true;
                                _endQueued = false;
                                _room = false;
                            }

                            Sync.IncrementSyncpoint(_syncpoint);
                            _framesOut++;

                            if (frame.End && _inputs.Count == 0 && _decoded.Count == 0 && DrainAtEnd())
                            {
                                DrainFreeAsEndOfStream();
                            }

                            if (_framesOut == 1 || _framesOut % 600 == 0 || frame.End)
                            {
                                Logger.Info?.Print(LogClass.ServiceNv, $"{InterfaceName} frame {_framesOut} ({size.width}x{size.height}, pts {frame.Pts} us{(frame.End ? ", end of stream" : "")}) in image {image}{(frame.Picture == null ? " (no picture)" : "")}");
                            }

                            continue;
                        }

                        Input input = _inputs.Dequeue();

                        if (!_streamEnded && !_endQueued)
                        {
                            _room = _inputs.Count < RefuseAt;
                        }

                        string codec = _codec;

                        Monitor.Exit(_lock);

                        try
                        {
                            pictures.Clear();

                            if (!_noDecode)
                            {
                                if (decoder == null || decoder.Name != codec)
                                {
                                    decoder?.Dispose();
                                    decoder = new Nx2VideoDecoder(codec);
                                }

                                decoder.Decode(input.Data, input.Pts, pictures);

                                if (input.End)
                                {
                                    decoder.Drain(pictures);
                                }
                            }
                            else if (input.Data.Length != 0)
                            {
                                pictures.Add(null);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error?.Print(LogClass.ServiceNv, $"{InterfaceName} decode failed: {e.Message}");
                        }
                        finally
                        {
                            Monitor.Enter(_lock);
                        }

                        if (_flush)
                        {
                            continue;
                        }

                        // Without a decoder every input still yields a (blank) frame, so the client's
                        // frame count and fences move on.
                        if (pictures.Count == 0 && input.Data.Length != 0 && (decoder == null || !decoder.IsOpen))
                        {
                            pictures.Add(null);
                        }

                        foreach (Nx2Picture picture in pictures)
                        {
                            _decoded.AddLast(new Decoded(picture, picture?.Pts ?? input.Pts, false));
                        }

                        if (input.End)
                        {
                            // The client stops at the frame marked end of stream; give it one even when
                            // the last input held no picture.
                            if (_decoded.Count == 0)
                            {
                                _decoded.AddLast(new Decoded(null, input.Pts, true));
                            }
                            else
                            {
                                Decoded last = _decoded.Last.Value;
                                _decoded.Last.Value = last with { End = true };
                            }
                        }
                    }
                }
            }
            finally
            {
                decoder?.Dispose();
            }
        }

        // Waits (bounded) until the client's reads of an image it gave back have passed.
        private void WaitForRelease(Fence fence)
        {
            if (fence.Syncpoint >= SynchronizationManager.MaxHardwareSyncpoints)
            {
                return;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(100);

            while ((int)(Sync.GetSyncpointValue(fence.Syncpoint) - fence.Value) < 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }
        }

        // Writes a decoded frame into an output image; returns the size the frame reports.
        private (uint, uint) WriteImage(uint imageId, Decoded frame, ref byte[] bytes)
        {
            ulong address = 0;
            Nx2DecoderImageLayout layout;

            lock (_objects.Lock)
            {
                if (!_objects.Images.TryGetValue(imageId, out Nx2NativeObjects.Image image) || !image.Decoder)
                {
                    Trace($"write skipped image={imageId}: unknown");

                    return (_width, _height);
                }

                layout = new Nx2DecoderImageLayout(image.Preference);

                if (_objects.Memories.TryGetValue(image.MemoryId, out Nx2NativeObjects.Memory memory) && memory.Address != 0)
                {
                    address = memory.Address + image.Offset;
                }
            }

            uint width = Math.Min(_width == 0 ? layout.Width : _width, layout.Width);
            uint height = Math.Min(_height == 0 ? layout.Height : _height, layout.Height);

            _memory ??= _clientMemory();

            if (frame.Picture == null || address == 0 || _memory == null)
            {
                Trace($"write skipped image={imageId} address={address:x} picture={frame.Picture != null} eos={frame.End}");

                return (width, height);
            }

            width = Math.Min((uint)frame.Picture.Width, layout.Width);
            height = Math.Min((uint)frame.Picture.Height, layout.Height);

            if (bytes.Length < (int)layout.Size)
            {
                bytes = new byte[layout.Size];
            }

            Nx2VideoDecoder.Render(frame.Picture, layout, (int)width, (int)height, bytes);

            ReadOnlySpan<byte> luma = bytes.AsSpan(0, (int)(layout.Pitch * height));
            ReadOnlySpan<byte> chroma = layout.Nv12
                ? bytes.AsSpan((int)layout.ChromaOffset, (int)(layout.Pitch * ((height + 1) / 2)))
                : [];

            try
            {
                if (_untrackedWrites)
                {
                    ((IWritableBlock)_memory).WriteUntracked(address, luma);
                    ((IWritableBlock)_memory).WriteUntracked(address + layout.ChromaOffset, chroma);
                }
                else
                {
                    _memory.Write(address, luma);
                    _memory.Write(address + layout.ChromaOffset, chroma);
                }
            }
            catch (Exception e) when (e is not InvalidMemoryRegionException && !_untrackedWrites)
            {
                // A tracking handler failed on this thread: the client copies the image out with the CPU
                // (nn::codec::ReadDecoderOutputImage), so untracked writes still reach it.
                Logger.Warning?.Print(LogClass.ServiceNv, $"{InterfaceName} tracked write of image {imageId} at {address:x} failed, writing untracked from now on: {e}");
                _untrackedWrites = true;
                ((IWritableBlock)_memory).WriteUntracked(address, luma);
                ((IWritableBlock)_memory).WriteUntracked(address + layout.ChromaOffset, chroma);
            }
            catch (Exception e)
            {
                if (_writeFailures++ < 4)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"{InterfaceName} couldn't write image {imageId} at {address:x}: {e}");
                }
            }

            Trace($"write image={imageId} address={address:x} {width}x{height} pitch={layout.Pitch} pts={frame.Pts} eos={frame.End}");

            return (width, height);
        }

        public void Dispose()
        {
            _readyTimer?.Dispose();

            lock (_lock)
            {
                _quit = true;
                Monitor.PulseAll(_lock);
            }

            if (_syncpoint != NoSyncpoint && _device != null)
            {
                _device.System.HostSyncpoint.ReleaseSyncpoint(_syncpoint);
                _syncpoint = NoSyncpoint;
            }
        }
    }
}

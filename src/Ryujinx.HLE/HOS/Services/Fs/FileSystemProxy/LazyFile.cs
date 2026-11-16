using LibHac;
using LibHac.Common;
using LibHac.Fs;
using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.HLE.HOS.Services.Fs.FileSystemProxy
{
    class LazyFile : LibHac.Fs.Fsa.IFile
    {
        private readonly LibHac.Fs.Fsa.IFileSystem _fs;
        private readonly string _filePath;
        private UniqueRef<LibHac.Fs.Fsa.IFile> _fileReference;
        private readonly FileInfo _fileInfo;
        private readonly long _size = -1;

        public LazyFile(string filePath, string prefix, LibHac.Fs.Fsa.IFileSystem fs)
        {
            _fs = fs;
            _filePath = filePath;
            _fileInfo = new FileInfo(prefix + "/" + filePath);
        }

        public LazyFile(string filePath, long size, LibHac.Fs.Fsa.IFileSystem fs)
        {
            _fs = fs;
            _filePath = filePath;
            _size = size;
        }

        // Unpacked RomFS folders hold hundreds of thousands of files: only the most recently read ones stay
        // open, or the process runs out of file descriptors.
        private const int MaxOpenFiles = 256;

        private static readonly object _openFilesLock = new();
        private static readonly LinkedList<LazyFile> _openFiles = new();

        private LinkedListNode<LazyFile> _openNode;

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            lock (_openFilesLock)
            {
                if (_fileReference.Get == null)
                {
                    if (_openFiles.Count >= MaxOpenFiles)
                    {
                        LazyFile oldest = _openFiles.Last!.Value;

                        _openFiles.RemoveLast();
                        oldest._openNode = null;
                        oldest._fileReference.Reset();
                    }

                    Result result = _fs.OpenFile(ref _fileReference.Ref, _filePath.ToU8Span(), OpenMode.Read);

                    if (result.IsFailure())
                    {
                        bytesRead = 0;

                        return result;
                    }

                    _openNode = _openFiles.AddFirst(this);
                }
                else if (_openNode != _openFiles.First)
                {
                    _openFiles.Remove(_openNode!);
                    _openFiles.AddFirst(_openNode!);
                }

                return _fileReference.Get!.Read(out bytesRead, offset, destination);
            }
        }

        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option)
        {
            throw new NotSupportedException();
        }

        protected override Result DoFlush()
        {
            throw new NotSupportedException();
        }

        protected override Result DoSetSize(long size)
        {
            throw new NotSupportedException();
        }

        protected override Result DoGetSize(out long size)
        {
            size = _size >= 0 ? _size : _fileInfo.Length;

            return Result.Success;
        }

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
        {
            throw new NotSupportedException();
        }
    }
}

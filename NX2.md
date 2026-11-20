# Switch 2 (NX2) branch: build, run, debug

This branch (`nx2`) runs Switch 2 games on macOS (Apple Silicon). It is tested with Mario Kart World.
The reference implementation is Lichii (a yuzu/Eden fork); its `NX2_NOTES.md` explains most of the
console behaviour this port follows.

## 1. Build

Requirements: macOS on Apple Silicon, the .NET 10 SDK (`dotnet`, often in `/usr/local/share/dotnet`).

```sh
dotnet build src/Ryujinx -c Release
codesign --entitlements distribution/macos/entitlements.xml -f -s - src/Ryujinx/bin/Release/net10.0/Ryujinx
```

The `codesign` step is required: without the hypervisor entitlement the CPU cannot run.

Mesh shaders need the MoltenVK fork with `VK_EXT_mesh_shader`: repository `HyperCed/Ryujinx.MoltenVK`,
branch `lichii-mesh`. Copy the `libMoltenVK.dylib` you build from it over both copies in the output:

```sh
cp libMoltenVK.dylib src/Ryujinx/bin/Release/net10.0/osx-arm64/libMoltenVK.dylib
cp libMoltenVK.dylib src/Ryujinx/bin/Release/net10.0/runtimes/osx-arm64/native/libMoltenVK.dylib
```

Without it the game still runs: task/mesh draws then go through a slower compute fallback.

If the build fails in `CreateAppHost`, build with `-p:UseAppHost=false` and run
`dotnet src/Ryujinx/bin/Release/net10.0/Ryujinx.dll` instead.

Check for `error` lines in the build output: a failed build leaves the previous binary in place.

## 2. Run

You need `prod.keys` and a firmware installed in `~/Library/Application Support/Ryujinx`, and the game
unpacked (`exefs/` and `romfs/` side by side).

Recommended: the wrapper, which adds a memory watchdog and keeps the Mac awake:

```sh
RYUJINX_NX2_MAXWELL=1 RYUJINX_NX2_COMPUTE=1 tools/nx2/run.sh /path/to/game/exefs run.log 120
```

Arguments: the exefs directory, the log file, and the run time in seconds. Any further arguments are
passed to Ryujinx. The equivalent direct command is:

```sh
RYUJINX_NX2_MAXWELL=1 RYUJINX_NX2_COMPUTE=1 RYUJINX_GPU_HANG_ABORT=1 RYUJINX_GPU_WATCHDOG_MS=1500 \
  src/Ryujinx/bin/Release/net10.0/Ryujinx --no-gui --disable-file-logging --disable-ptc \
  --disable-shader-cache --dram-size MemoryConfiguration32GiB /path/to/game/exefs > run.log 2>&1
```

### Required settings

| Setting | Why |
|---|---|
| `RYUJINX_NX2_MAXWELL=1` | Runs the Switch 2 3D commands and shaders on the Maxwell engine. Without it nothing is drawn. |
| `RYUJINX_NX2_COMPUTE=1` | Runs compute dispatches (lighting, sky, culling). Without it the scene is mostly missing. |
| `--dram-size MemoryConfiguration32GiB` | The game's GPU-only memory comes from the extra system pool. With less memory it runs out. |
| `--disable-shader-cache` | Switch 2 programs must not go through the Maxwell disk cache. |

### Recommended settings

| Setting | Why |
|---|---|
| `RYUJINX_GPU_HANG_ABORT=1`, `RYUJINX_GPU_WATCHDOG_MS=1500` | Stop the process when the host GPU hangs. On a Mac a hung GPU freezes the whole screen. |
| `--no-gui` | Headless window (the only mode tested). |
| `--disable-ptc` | No CPU translation cache. |
| `--use-hypervisor false` | Runs the CPU with the JIT instead of the Apple hypervisor (slower, to compare when a bug shows up). |

### Rules

- Run only one Ryujinx at a time: they share the save directory.
- Watch the memory. The game uses 8-11 GB; when a 16 GB Mac runs out, WindowServer can crash. The
  wrapper stops the run when Ryujinx passes `MAX_FOOT_MB` (default 12500; `footprint` rounds to the GB above 10 GB) or the system's free memory
  level drops under `MIN_FREE_PCT` (default 12%). It then prints `WATCHDOG: stopping Ryujinx: …`: that
  is not a crash. Close other applications, or lower the limit with care (`MIN_FREE_PCT=8`).
- Loading takes about 45 s before the GPU starts; the title screen appears about 15 s later.
- The log mixes text and binary: use `grep -a`.

### Tuning options

| Variable | Default | Effect |
|---|---|---|
| `MVK_CONFIG_SPARSE_HEAP_SIZE_MB` | 1024 (set by `tools/nx2/run.sh`) | Size of the MoltenVK sparse heap the virtual textures take their blocks from. Metal commits it whole; the fork's own default (3072) runs a 16 GB Mac out of memory. |
| `RYUJINX_NX2_NO_SPARSE_TEXTURES` | off | `1`: virtual textures are no longer sparse host textures, but ordinary ones without their largest mip levels (see below). |
| `RYUJINX_NX2_SPARSE_TEXTURE_MAX_MB` | 512 | Without sparse textures: largest host size of a virtual texture; the largest mip levels are dropped to fit. |
| `RYUJINX_NX2_VB_MARGIN` | 0x10000 | Extra bytes added to vertex buffers (Metal faults on reads past the end). |
| `RYUJINX_NX2_RELEASE_SYNC` | off | `1`: each semaphore release creates the host sync that makes GPU-written buffers readable by the CPU (as a syncpoint does). Experimental: the title screen then crashes after 25-60 s. |
| `RYUJINX_NX2_BACK_SPARSE` | on | `0`: sparse regions up to 512 MiB are left without backing memory. |
| `RYUJINX_NX2_SPARSE_OVERRIDE` | off | `1`: the game's sparse mappings replace the backing of backed regions. |
| `RYUJINX_NX2_NO_MESH`, `RYUJINX_NX2_NO_TESSELLATION` | off | `1`: skip mesh or tessellation draws. |
| `RYUJINX_NX2_NO_IMAGE_BOUNDS` | off | `1`: no bounds checks on image stores and atomics. |
| `RYUJINX_NX2_ACQUIRE_TIMEOUT` | off | `1`: give up on a semaphore acquire that never completes instead of waiting. |
| `RYUJINX_NX2_MAXWELL_RANGE=<lo>-<hi>`, `RYUJINX_NX2_MAXWELL_BLOCK=<m>,<m>` | all | Forward only some 3D methods (hex) to Maxwell, to bisect a problem. |
| `RYUJINX_ZERO_FILL_ALL` | off | `1`: zero every new page, as before (to compare). |
| `RYUJINX_NX2_NO_VOLUME_SLICES` | off | `1`: render targets that are one slice of a 3D texture are no longer drawn into that texture (to compare). `RYUJINX_NX2_VOLUME_SLICES_WIDTH=<w>` limits it to one width. |
| `RYUJINX_NX2_NO_CB_DIRTY` | off | `1`: constant buffer updates no longer mark the buffer dirty (to compare; draws then read stale constants). |
| `RYUJINX_NX2_VT_SKIP=<hex>` | none | Leave the virtual texture at that descriptor address unbound (to see what it does). |
| `RYUJINX_NX2_VT_NO_SRGB` | off | `1`: sample the ASTC virtual textures as linear instead of sRGB (experiment). |
| `RYUJINX_NX2_NO_2D` | off | `1`: ignore the 2D engine (subchannel 3), as before. |
| `RYUJINX_NX2_HOST_SPARSE_COPIES` | off | `1`: 2D blits into sparse regions stay host texture copies (the virtual textures then never see their tiles). |

## 3. Debug

All the tools are environment variables, used with the run command above. Paths are directories that
must exist, unless noted.

### See the output

| Variable | Output |
|---|---|
| `RYUJINX_FRAME_CAPTURE=<dir>` | PNG of every Nth presented frame (`RYUJINX_FRAME_CAPTURE_EVERY=N`, default 120). |
| `RYUJINX_PRESENT_TRACE=1` | Logs render targets, draws into presented images, and their content. |

### Find a wrong render pass (graphics)

The pass dumper saves one frame, pass by pass, as PNG files plus statistics (min, max, mean, NaN and
Inf counts per channel) in `passes.txt`.

| Variable | Effect |
|---|---|
| `RYUJINX_NX2_PASS_DUMP=<dir>` | Dumps every render target as the game moves past it, and every image a compute dispatch writes. |
| `RYUJINX_NX2_PASS_DUMP_AT=<s>` | Which frame: the first presented after that many seconds of GPU time (default 100; about 40 reaches the title screen). |
| `RYUJINX_NX2_PASS_DUMP_PROGRAM=<hex>` | For draws using that shader address: vertex input, indices, every constant buffer (`.bin` files), and the textures sampled. |
| `RYUJINX_NX2_PASS_DUMP_RT=<hex>[-<hex>]` | Render target(s) at that GPU address (or range): saved after every draw into them. |
| `RYUJINX_NX2_PASS_DUMP_NOPNG=1` | Statistics only (needed for per-draw dumps of large targets). |
| `RYUJINX_NX2_PASS_DUMP_FRAMES=<n>` | Keep dumping for n presents (default 1). With `RYUJINX_NX2_PASS_DUMP_AT=1` and a target range, follows a render target over the whole run (passes drawn only at load time). |

Typical session:

1. Dump a frame. In `passes.txt`, find the first pass with `NaN` not 0, or with wrong colours. The
   draws listed before it name their shaders (`VS 0x… FS 0x…`).
2. Dump that render target per draw (`RYUJINX_NX2_PASS_DUMP_RT`) to find the exact draw.
3. Dump that draw's inputs (`RYUJINX_NX2_PASS_DUMP_PROGRAM`), then its shaders (below).

### Shaders

| Variable | Output |
|---|---|
| `RYUJINX_NX2_SHADER_DUMP=<dir>` | Every native Switch 2 program once, as `<key>-<stage>-<address>.bin`. |
| `RYUJINX_NX2_MESH_SPV_DUMP=<dir>` | SPIR-V of every task/mesh pipeline. |
| `RYUJINX_NX2_SPV_DUMP_PROGRAM=<hex>` | Also the SPIR-V of the pipelines using that program, into the directory above. For a compute program: `.Compute.spv`, plus the native code (`.native.bin`) and the code lowered to Maxwell (`.lowered.bin`). |
| `RYUJINX_NX2_SPV_DUMP=<dir>` | SPIR-V of every Switch 2 pipeline. |

Read SPIR-V with `spirv-opt -O x.spv -o y.spv && spirv-dis y.spv` (Homebrew `spirv-tools`). Native
code is 16 bytes per instruction (opcode in bits 0-11); lowered code is Maxwell code (8 bytes per
instruction, a scheduling word every 32 bytes).

### Values that go bad (NaN), CPU/GPU data exchange

| Variable | Effect |
|---|---|
| `RYUJINX_NX2_CB_WATCH=<program>:<stage>:<slot>:<word>` | Logs a constant buffer word of that program's draws when it becomes or stops being NaN (stage 4 = fragment). Finds when a value first goes bad. |
| `RYUJINX_NX2_FLUSH_TRACE=1` | Logs host syncs, CPU reads of GPU-written buffers, and read-back data that contains NaN. |
| `RYUJINX_NX2_SB_TRACE=1` | Logs storage buffer bindings: shader, read or written, address and size. Finds which shader writes a buffer. |
| `RYUJINX_HV_WATCH=<start>-<end>` (+ `RYUJINX_HV_WATCH_DIR=<dir>`) | Logs the game code (PC, LR) behind CPU accesses to that range of tracked GPU memory, and saves the code around each PC. Finds which game function writes some GPU data. |

### Memory, textures, sparse resources

| Variable | Effect |
|---|---|
| `RYUJINX_NX2_ALLOC_TRACE=1` | Logs buffers and textures of 32 MiB or more, and texture lookups that find no mapped memory. |
| `RYUJINX_NX2_TEXBIND_TRACE=1` | Logs texture bindings left null or with another target, with the shader using them. |
| `RYUJINX_NX2_TEXTURE_TRACE=1` | Raises the limit of the texture binding trace. |
| `RYUJINX_NX2_BIGTEX_TRACE=1` | Logs textures of 2048x2048 or more read from the texture pool. |
| `RYUJINX_NX2_SPARSE_TRACE=1` (+ `RYUJINX_NX2_SPARSE_TRACE_RANGE=<start>-<end>`) | Logs the sparse map requests (gpu 216), optionally only in an address range. |
| `RYUJINX_NX2_VB_TRACE=1` | Logs vertex buffer bindings and sizes (first 400). |
| `RYUJINX_NX2_RT_TRACE=1` |
| `RYUJINX_NX2_2D_TRACE=1` | Logs the first 60 2D engine blits (source and destination surfaces). |
| `RYUJINX_NX2_DMA_TRACE=<start>-<end>` | Logs the copy engine copies whose destination starts in that range. |
| `RYUJINX_NX2_VT_HASH=1` | Periodically logs how many uploaded ranges of each virtual texture changed in guest memory since. |
| `RYUJINX_NX2_VT_DUMP=<dir>` | Saves the raw guest bytes of the first ranges each virtual texture uploads. |
| `RYUJINX_NX2_VT_REFRESH=<n>` | Uploads everything mapped again every n refreshes (experiment). | Logs each distinct combination of shader and render targets (address, size, format, 3D layout) over the run, and the 3D textures the shaders sample. |

The 214 region creations are always logged (`nx2:gpu 214 … region`).

### Command streams, hangs, stalls

| Variable | Effect |
|---|---|
| `RYUJINX_NX2_GPFIFO_DUMP=<file>` | Every GPFIFO entry as it runs (channel, address, words), for offline study. |
| `RYUJINX_NX2_TRACE_CB=1` | Logs the constant buffer selector writes. |
| `RYUJINX_NX2_SEM_WATCH=<hex>` | Logs every semaphore operation on the 16 bytes at that GPU address. |
| `RYUJINX_NX2_THREAD_DUMP=1` | At the first 4 s present timeout: guest thread stacks and pending waits. |
| `RYUJINX_NX2_PB_REREAD=1` (+ `RYUJINX_NX2_PB_COMPARE=1`), `RYUJINX_NX2_PB_SNAPSHOT=0` | Command buffers are kept as submitted; these re-read them after a suspension (and log differences), or always read them when they run. |
| `RYUJINX_NX2_PARK_SPLIT_METHODS=1` | Older handling of methods split across GPFIFO entries. |
| `--enable-debug-logs`, `--enable-trace-logs` | Service calls (TIPC, CMIF) in the log. |

Host stacks of a stuck process: `dotnet-stack report -p <pid>` (`dotnet tool install -g dotnet-stack`).

## 4. Where the code is

- Switch 2 services: `src/Ryujinx.HLE/HOS/Services/Nx2/` (GPU memory: `INx2GpuSession.cs`).
- Switch 2 GPU engines and command processing: `src/Ryujinx.Graphics.Gpu/Engine/Nx2/`,
  `src/Ryujinx.Graphics.Gpu/Engine/GPFifo/`.
- Shader lowering (Switch 2 code to Maxwell code): `src/Ryujinx.Graphics.Gpu/Shader/Nx2/Nx2Lowering.cs`,
  ported from Lichii; tests in `src/Ryujinx.Tests/Graphics/Nx2/`.
- Pass dumper: `src/Ryujinx.Graphics.Gpu/Nx2PassDumper.cs`.

#!/bin/zsh
# usage: tools/nx2/run.sh <game exefs dir> <log file> <seconds> [extra Ryujinx args]
# Runs headless Ryujinx with a memory watchdog (a Mac whose memory runs out can take WindowServer down).
# Stops Ryujinx when its footprint passes MAX_FOOT_MB (default 16384), the system free memory level drops
# under MIN_FREE_PCT (default 6) or the time is up (<seconds> 0: no time limit). NO_WATCHDOG=1 keeps only
# the memory log: no limit at all. Memory samples go to <log>.mem.
EXEFS=$1; LOG=$2; SECS=$3; shift 3
ROOT=${0:A:h:h:h}
MAX_FOOT_MB=${MAX_FOOT_MB:-16384}
MIN_FREE_PCT=${MIN_FREE_PCT:-6}
NO_WATCHDOG=${NO_WATCHDOG:-0}

if pgrep -x Ryujinx >/dev/null; then echo "Ryujinx is already running"; exit 1; fi

# The mesh/sparse MoltenVK sparse heap (committed whole by Metal) holds the virtual textures' tiles; they grow past 1 GB.
export MVK_CONFIG_SPARSE_HEAP_SIZE_MB=${MVK_CONFIG_SPARSE_HEAP_SIZE_MB:-3072}

RYUJINX_GPU_HANG_ABORT=1 RYUJINX_GPU_WATCHDOG_MS=3000 caffeinate -dims \
  $ROOT/src/Ryujinx/bin/Release/net10.0/Ryujinx --no-gui --disable-file-logging --disable-ptc --disable-shader-cache \
  --dram-size ${DRAM:-MemoryConfiguration32GiB} "$@" "$EXEFS" > $LOG 2>&1 &
PID=$!
START=$(date +%s)
: > $LOG.mem
stop() {
  echo "WATCHDOG: stopping Ryujinx: $1 (not a crash; see MAX_FOOT_MB / MIN_FREE_PCT)" | tee -a $LOG.mem
  pkill -INT -x Ryujinx
  for i in $(seq 1 30); do sleep 0.5; pgrep -x Ryujinx >/dev/null || return; done
  pkill -9 -x Ryujinx
}
while kill -0 $PID 2>/dev/null; do
  T=$(($(date +%s) - START))
  RPID=$(pgrep -x Ryujinx | head -1)
  FOOT=$(footprint ${RPID:-0} 2>/dev/null | sed -n 's/.*Footprint: \([0-9.]*\) \([KMG]\)B.*/\1 \2/p' | head -1 | awk '{n=$1; if($2=="G")n*=1024; else if($2=="K")n/=1024; printf "%d", n}')
  FOOT=${FOOT:-0}
  FREE=$(sysctl -n kern.memorystatus_level)
  echo "$T $FOOT $FREE" >> $LOG.mem
  if [ $NO_WATCHDOG = 1 ]; then sleep 1; continue; fi
  if [ $FOOT -gt $MAX_FOOT_MB ]; then stop "footprint ${FOOT} MB > ${MAX_FOOT_MB} MB at ${T}s"; break; fi
  if [ $FREE -lt $MIN_FREE_PCT ]; then stop "free memory ${FREE}% < ${MIN_FREE_PCT}% at ${T}s"; break; fi
  if [ $SECS -gt 0 ] && [ $T -ge $SECS ]; then stop "time limit ${SECS}s"; break; fi
  sleep 0.3
done
wait $PID 2>/dev/null
echo "peak footprint $(grep -v WATCHDOG $LOG.mem | sort -k2 -n | tail -1 | awk '{print $2}') MB"

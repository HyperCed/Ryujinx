#!/bin/zsh
# usage: tools/nx2/run.sh <game exefs dir> <log file> <seconds> [extra Ryujinx args]
# Runs headless Ryujinx with a memory watchdog (a Mac whose memory runs out can take WindowServer down).
# Stops Ryujinx when its footprint passes MAX_FOOT_MB (default 12500), the system free memory level drops
# under MIN_FREE_PCT (default 12) or the time is up. Memory samples go to <log>.mem.
EXEFS=$1; LOG=$2; SECS=$3; shift 3
ROOT=${0:A:h:h:h}
MAX_FOOT_MB=${MAX_FOOT_MB:-12500}
MIN_FREE_PCT=${MIN_FREE_PCT:-12}

if pgrep -x Ryujinx >/dev/null; then echo "Ryujinx is already running"; exit 1; fi

# The mesh/sparse MoltenVK commits its sparse heap whole (3 GB by default): the virtual textures need ~0.7 GB.
export MVK_CONFIG_SPARSE_HEAP_SIZE_MB=${MVK_CONFIG_SPARSE_HEAP_SIZE_MB:-1024}

RYUJINX_GPU_HANG_ABORT=1 RYUJINX_GPU_WATCHDOG_MS=1500 caffeinate -dims \
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
  if [ $FOOT -gt $MAX_FOOT_MB ]; then stop "footprint ${FOOT} MB > ${MAX_FOOT_MB} MB at ${T}s"; break; fi
  if [ $FREE -lt $MIN_FREE_PCT ]; then stop "free memory ${FREE}% < ${MIN_FREE_PCT}% at ${T}s"; break; fi
  if [ $T -ge $SECS ]; then stop "time limit ${SECS}s"; break; fi
  sleep 0.3
done
wait $PID 2>/dev/null
echo "peak footprint $(grep -v WATCHDOG $LOG.mem | sort -k2 -n | tail -1 | awk '{print $2}') MB"

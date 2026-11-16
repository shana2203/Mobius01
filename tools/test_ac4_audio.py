"""Run the real AC4 streaming implementation against synthetic ADPCM blocks."""
from pathlib import Path
import subprocess
import tempfile
from test_support import compiler_environment

root = Path(__file__).resolve().parents[1]
source = (root / 'runtime/src/ps2_hle_ac4.c').read_text()
start = source.index('#define VAG_CHANNELS')
end = source.index('static int vag_rpc(', start)
prelude = r'''
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
typedef uint8_t u8; typedef uint32_t u32; typedef uint64_t u64;
typedef int16_t s16; typedef int64_t s64;
#define PS2_IOP_RAM_SIZE (2u << 20)
#define PS2_IOP_RAM_BASE 0x1c000000u
static u8 ram[PS2_IOP_RAM_SIZE];
static s16 gains[48][2];
static u64 field;
static void ps2_log(const char *fmt, ...) { (void)fmt; }
static u64 ps2_kernel_vblank_count(void) { return field; }
static void ps2_get_mem(void *out, u32 addr, u32 size) {
    memcpy(out, ram + (addr & (PS2_IOP_RAM_SIZE-1)), size);
}
static void ps2_spu2_decode_block(const u8 *blk, s16 *out, int *s1, int *s2, u32 *hdr) {
    /* Decoder is separately tested: give each ADPCM block an identifiable constant. */
    for (int i=0; i<28; i++) out[i] = blk[2] * 128;
}
static void ps2_spu2_voice_volume(u32 i, s16 *l, s16 *r) {
    *l=gains[i][0]; *r=gains[i][1];
}
'''
test = r'''
int main(void) {
    vag_base = 0x40000;
    vag_spu_voice[0] = 24;
    gains[24][0] = gains[24][1] = 16384; /* half-volume mono */
    u32 addr = vag_base + 0x180;
    ram[addr+2] = 64;
    ps2_ac4_iop_fill(addr,16);
    vag_begin(0,16,1,4096);
    s16 out[60] = {0};
    vag_run(out,28);
    assert(vag[0].status == VAG_ST_PLAY);
    for (int i=1; i<28; i++) assert(out[2*i] == 4096 && out[2*i+1] == 4096);
    vag_run(out+56,1);
    assert(vag[0].status == VAG_ST_IDLE);
    /* A refill in a recycled low slot must follow an older same-field refill. */
    memset(vag_fills,0,sizeof vag_fills);
    ps2_ac4_iop_fill(addr,16);
    ps2_ac4_iop_fill(addr+16,16);
    vag_fills[0].used=1;
    ps2_ac4_iop_fill(addr+32,16);
    assert(vag_next_fill(&vag[0]) == 1);
    /* Stereo voices have independent pan/volume, including complete mute. */
    memset(vag_fills,0,sizeof vag_fills);
    ram[addr+2]=32; ram[addr+2048+2]=64;
    gains[24][0]=16384; gains[24][1]=0;
    gains[25][0]=0; gains[25][1]=8192;
    ps2_ac4_iop_fill(addr,4096);
    vag_begin(0,2048,2,4096);
    memset(out,0,sizeof out);
    vag_run(out,3);
    assert(out[2]==2048 && out[3]==2048);
    memset(gains,0,sizeof gains);
    memset(out,0,sizeof out);
    vag_run(out,3);
    for (int i=0;i<6;i++) assert(out[i]==0);
    /* Real startup pattern: sound-bank staging and music reuse IOP RAM.
       Partial overlaps must invalidate old descriptions, not replay them. */
    memset(vag_fills,0,sizeof vag_fills);
    ps2_ac4_iop_fill(0xA0180,67584);
    ps2_ac4_iop_fill(0xA0180,196608);
    ps2_ac4_iop_fill(0xD0180,196608);
    ps2_ac4_iop_fill(0xA0180,176128);
    ps2_ac4_iop_fill(0xC0180,131072);
    vag_begin(1,10584064,2,3764);
    int next=vag_next_fill(&vag[1]);
    assert(next>=0 && vag_fills[next].addr==0xC0180 && vag_fills[next].size==131072);
    int live=0; for(int i=0;i<VAG_FILLS;i++) live+=vag_fills[i].size && !vag_fills[i].used;
    assert(live==1);
    /* A current oversized staging read is never a playable ring buffer. */
    ps2_ac4_iop_fill(0xE0180,196608);
    next=vag_next_fill(&vag[1]);
    assert(next>=0 && vag_fills[next].addr==0xC0180);
    /* A new write also detaches an active reference to overwritten memory. */
    vag[1].fill=next; vag[1].pos=128;
    ps2_ac4_iop_fill(0xC0180,131072);
    assert(vag[1].fill==-1 && vag[1].pos==0);
    /* Radio lines (EE channel 4): the game reads the next line into the
       channel's next buffer while the current one is still sounding.  The
       end of the current line must not throw that prefetch away, or the new
       line never gets data and the game waits for it forever (Mission 01). */
    memset(vag_fills,0,sizeof vag_fills);
    memset(gains,0,sizeof gains);
    /* channel 1's ring shares IOP memory with the radio channels: idle it */
    for (int i=0;i<VAG_IOP_CHANS;i++) vag[i].status = VAG_ST_IDLE;
    vag_spu_voice[2] = 30;
    gains[30][0] = gains[30][1] = 16384;
    u32 radio = vag_base + 0x180 + 2u*0x30000u;
    ram[radio+2] = 64;
    ps2_ac4_iop_fill(radio,16);
    vag_begin(4,16,1,4096);
    s16 buf[128];
    vag_run(buf,10);                      /* line A is sounding */
    ram[radio+0x10000+2] = 96;
    ps2_ac4_iop_fill(radio+0x10000,16);   /* line B prefetched */
    vag_run(buf,40);                      /* line A ends */
    assert(vag[2].status == VAG_ST_IDLE && vag[2].finished);
    int live_b=0;
    for (int i=0;i<VAG_FILLS;i++)
        if (vag_fills[i].size && !vag_fills[i].used && vag_fills[i].addr==radio+0x10000) live_b=1;
    assert(live_b);
    vag_begin(4,16,1,4096);
    memset(buf,0,sizeof buf);
    vag_run(buf,20);
    assert(buf[2*5] == 6144);             /* line B plays: 96*128 at half volume */
    puts("PASS: final block, refill ordering, stereo/volume/mute, overlapping staging reads, oversized transfers, active buffer invalidation, radio prefetch across line end");
}
'''
compiler, env = compiler_environment()
with tempfile.TemporaryDirectory() as tmp:
    tmp = Path(tmp)
    (tmp/'test.c').write_text(prelude + source[start:end] + test)
    subprocess.run([compiler,'-std=gnu2x','-O2',str(tmp/'test.c'),
                    '-o',str(tmp/'test.exe'),'-lpthread'], check=True, env=env)
    subprocess.run([str(tmp/'test.exe')], check=True, env=env)

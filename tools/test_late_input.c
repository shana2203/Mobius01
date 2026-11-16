/* Synthetic input published between vblank and guest read; no controller
   or game data needed. Build with -O2 -flto -fwhole-program and include the
   runtime/include directory; LTO removes unrelated guest-memory handlers. */
#include <assert.h>
#include <stdio.h>
#include "../runtime/src/ps2_hle_pad.c"

static int input_hooks;
int ps2_modapi_has_input_hooks(void) { return input_hooks; }
void ps2_log(const char *fmt, ...) { (void)fmt; }

int main(int argc, char **argv) {
    ps2_pad_state old = {0}, newer, result;
    int fallback = argc > 1;
    _putenv_s("PS2_LATE_INPUT", fallback && !strcmp(argv[1], "off") ? "0" : "1");
    if (fallback && !strcmp(argv[1], "record")) _putenv_s("PS2_PAD_RECORD", "test-only");
    if (fallback && !strcmp(argv[1], "replay")) _putenv_s("PS2_PAD_PLAY", "test-only");
    ps2_pad_init();
    old = ps2_pad_host[0];
    newer = old;
    newer.lx = 220;
    newer.buttons = 1u << PAD_CROSS;
    ps2_pad_publish(0, &newer);
    pad_read_snapshot(0, &result);
    if (fallback) {
        assert(result.lx == old.lx && result.buttons == old.buttons);
        puts("field-latched fallback: PASS");
        return 0;
    }
    assert(result.lx == 220 && result.buttons == newer.buttons);
    assert(ps2_pad_host[0].lx == old.lx); /* no vblank or global mutation */
    newer.buttons = 0;
    ps2_pad_publish(0, &newer);
    pad_read_snapshot(0, &result);
    assert(result.buttons == 0); /* release is visible without another field */

    newer.buttons = 1u << PAD_CIRCLE;
    ps2_pad_publish(0, &newer);
    newer.buttons = 0;
    ps2_pad_publish(0, &newer);
    pad_read_snapshot(0, &result);
    assert(result.buttons == (1u << PAD_CIRCLE)); /* short tap is not lost */
    pad_read_snapshot(0, &result);
    assert(result.buttons == 0); /* short tap delivered once */

    newer.buttons = 1u << PAD_SQUARE;
    ps2_pad_publish(0, &newer);
    pad_read_snapshot(0, &result);
    ps2_pad_publish(0, &newer); /* repeated held samples aren't new presses */
    newer.buttons = 0;
    ps2_pad_publish(0, &newer);
    pad_read_snapshot(0, &result);
    assert(result.buttons == 0);

    input_hooks = 1;
    pad_read_snapshot(0, &result);
    assert(result.lx == old.lx && result.buttons == old.buttons);
    input_hooks = 0;
    pad_rec_fp = tmpfile();
    assert(pad_rec_fp);
    pad_read_snapshot(0, &result);
    assert(result.lx == old.lx);
    fclose(pad_rec_fp);
    pad_rec_fp = NULL;
    pad_play_buf = &old;
    pad_read_snapshot(0, &result);
    assert(result.lx == old.lx);
    pad_play_buf = NULL;

    newer.lx = 42;
    ps2_pad_publish(1, &newer);
    pad_read_snapshot(1, &result);
    assert(result.lx == 42);
    puts("latest sample, release, short taps, held buttons, mods, record/replay, port isolation: PASS");
    return 0;
}

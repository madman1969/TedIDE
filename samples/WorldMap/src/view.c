#include <string.h>
#include <conio.h>
#include <ctype.h>
#include "farmem.h"
#include "screen.h"
#include "world.h"
#include "view.h"

/* The text screen and its colours, which conio and the overviews use (screen.h). The map is
 * drawn through screen_draw_chars/colours instead, as on the Plus/4 that's a hidden screen. */
#define SCREEN       ((unsigned char*)SCREEN_CHARS)
#define COLOURS      ((unsigned char*)SCREEN_COLOURS)

#define OVERVIEW_TOP   3
#define OVERVIEW_ROWS  16

/* The character for each tile: its picture's, drawn by screen_c64.c or screen_plus4.c. Filled
 * in by view_init. Not static: blit.s reads it, and the tiles' colours (view_tile_colour, in
 * those files) too. */
unsigned char view_tile_char[TILE_COUNT];

/* The status bar's name for each picture - every shade of a kind has the same one. */
static const char* const glyph_name[GLYPH_TOWN + 1] = {
    "deep sea", "sea", "beach", "grass", "forest", "hills", "mountain", "snow", "river", "town",
};

#define CURSOR_CHAR    (SCREEN_FIRST_GLYPH + GLYPH_CURSOR)
#define CURSOR_OFFSET  ((SCREEN_MAP_TOP + CURSOR_Y) * SCREEN_COLS + CURSOR_X)

/* How the map's rows reach the screen (blit.s). The C64 draws a whole frame at once, racing the
 * raster beam, from pointers to each row's tiles in the cache, split into low and high bytes. The
 * Plus/4 draws a range of rows into its hidden screen, checking the time before each, so it can
 * draw ahead a few rows at a time; it finds each row's tiles itself, from the first row's cache
 * slot and the column (blit_tiles: that column in slot 0). */
#if VIEW_FINE_BITS == 0
unsigned char view_row_lo[VIEW_HEIGHT];
unsigned char view_row_hi[VIEW_HEIGHT];
void view_blit(void);
#else
extern const unsigned char* blit_tiles;
extern unsigned char blit_slot;
extern unsigned char* blit_chars;
extern unsigned char* blit_colours;
extern unsigned char blit_end;
extern unsigned char blit_stop_line;
unsigned char __fastcall__ view_blit_rows(unsigned char first);
#endif

/* The tile cache: map rows, each in a slot with room for every column. Map row y lives in slot
 * y % CACHE_ROWS, and there are two more slots than the view has rows, so the row just above and
 * the row just below the view can be read ahead of time without overwriting one on show.
 * cache_row says which map row each slot holds, and cache_lo and cache_hi which of its columns:
 * CHUNKs lo to hi - 1, none if hi is 0. All three are bytes - maps are at most 256 rows high and
 * 512 columns wide, 16 CHUNKs (world.c) - as cc65 is far quicker with bytes than with ints. The
 * C64, whose REU copies a row in no time, reads every column. On the Plus/4 a whole row takes
 * about a frame to read from a RAM expansion, so it reads the view's columns first, then more on
 * either side when there's time to spare, so that scrolling sideways finds them there. */
#define CACHE_ROWS   (VIEW_HEIGHT + 2)
#define CACHE_W      512                /* the widest map, world.c */
#define NO_ROW       0xFFFF
#define CHUNK        32                 /* columns are read in multiples of this... */
#define CHUNK_SHIFT  5                  /* ...which is 1 << CHUNK_SHIFT */
static unsigned char cache[CACHE_ROWS][CACHE_W];
static unsigned char cache_row[CACHE_ROWS];
static unsigned char cache_lo[CACHE_ROWS];
static unsigned char cache_hi[CACHE_ROWS];
static unsigned char* cache_line[CACHE_ROWS];   /* each slot's address, filled in by view_init */

/* The cache slot of map row y - y % CACHE_ROWS, from a table filled in by view_init, as a
 * division in cc65's C takes about as long as drawing a row. Maps are at most 256 rows high
 * (world.c). */
static unsigned char slot_table[256];
#define slot_of(y)  slot_table[(unsigned char)(y)]

/* Whether the status bar needs setting up again, after the screen was cleared. */
static unsigned char status_dirty;

/* The top-left cell of the frame on show (its "key" - see frame_key), to tell a move within it
 * from one out of it, and the cell its cursor is over. */
static unsigned drawn_left;
static unsigned drawn_top;
static unsigned char drawn_valid;
static unsigned cursor_x;
static unsigned cursor_y;

/* The last view position drawn, to see which way the view is moving. */
static unsigned last_left;
static unsigned last_top;

#if VIEW_FINE_BITS
/* Drawing ahead (Plus/4): the frame for the cell the view is heading into, drawn into the hidden
 * screen a few rows at a time, so that when the view gets there the screens just switch. */
static unsigned ahead_left;
static unsigned ahead_top;
static unsigned char ahead_slot;        /* the cache slot of its first row */
static unsigned char ahead_a, ahead_b;  /* the CHUNKs of columns its rows need */
static unsigned char ahead_ready;       /* rows, cursor and status bar all drawn */
static unsigned char ahead_status;      /* the status bar drawn */
static unsigned char ahead_bg_row;      /* the next row to widen in spare time (widen_rows) */

/* Each row of the frame being drawn ahead: */
#define ROW_TO_DRAW  0                  /* its columns are in the cache - draw it */
#define ROW_TO_READ  1                  /* some of them have to be read first */
#define ROW_DRAWN    2
static unsigned char ahead_state[VIEW_HEIGHT];
static unsigned char ahead_to_draw;     /* how many are ROW_TO_DRAW */
static unsigned char ahead_draw_from;   /* the first not ROW_DRAWN, or before it */
/* The ROW_TO_READ rows, in order, and the next of them to read: going through all the rows in C
 * to find them takes much of the moment a read can start in. */
static unsigned char ahead_reads[VIEW_HEIGHT];
static unsigned char ahead_read_count;
static unsigned char ahead_read_next;

/* The last prediction of the next key, and what it was worked out from: while the view keeps
 * moving the same way from the same frame, the answer is the same, and working it out in C
 * takes a good part of a frame. */
static int pred_dx, pred_dy;
static unsigned pred_from_left, pred_from_top = NO_ROW;
static unsigned pred_left, pred_top;

/* Raster lines on the Plus/4, measured in VICE with some to spare, for drawing ahead without
 * running on past the frame's end - which would make the next step late. The next step has to
 * start (view_draw) by about line 195, and main.c's loop takes up to 50 lines to get there, so
 * drawing ahead stops well before line 204: drawing rows on ROWS_STOP_LINE (blit.s checks before
 * each row, and a row takes up to 20 lines); the status bar, in C, needs STATUS_LINES left; a read
 * from far memory, whose copy gets screen_far_read_lines, leaves STEP_LINES of those and takes
 * READ_CHUNK_LINES of them a CHUNK - copied into far_low_buf and out again, at about 13 cycles a
 * byte each way, with the TED leaving the CPU 55 cycles a line. */
#define ROWS_STOP_LINE    100
#define STATUS_LINES      110
#define STEP_LINES        60
#define READ_CHUNK_LINES  16
#define READ_MAX          7                 /* CHUNKs: FarMem copies up to 256 bytes at once */
#define WAIT_STOP_LINE    20                /* rows drawn while a read waits for the split */

/* How many CHUNKs either side of the view's columns rows are widened to in spare time
 * (widen_rows). */
#define SPARE_CHUNKS      2
#endif

static char backend_name[13];

/* The screen code for a PETSCII character: letters in a C string are PETSCII, which the screen
 * shows 64 codes lower. */
static unsigned char to_screen(unsigned char c)
{
    return (c >= 0x40 && c < 0x60) ? c - 0x40 : c;
}

/* Tables for the status bar's numbers, filled in by view_init, as dividing in cc65's C takes far
 * longer: for n (0-511) - map coordinates - its hundreds digit (or a space) is
 * number_hundreds[n >> 2], as hundreds start on multiples of 4; then for the rest, 0-99, the tens
 * digit (or a space, with no hundreds - number_tens_lead) and the ones digit. Screen codes for
 * digits and spaces are the same as their PETSCII. */
static unsigned char number_hundreds[128];
static unsigned number_hundreds_value[128];    /* ...and what the hundreds come to */
static unsigned char number_tens[100];
static unsigned char number_tens_lead[100];
static unsigned char number_ones[100];

/* The status bar's name for each tile, in screen codes padded to 8 characters (view_init). */
static unsigned char tile_text[TILE_COUNT][8];

/* The map column each overview column samples. */
static unsigned overview_x[SCREEN_COLS];

/* Puts one tile's character and colour on the text screen. */
static void put_tile(unsigned char x, unsigned char y, unsigned char tile)
{
    unsigned offset = y * SCREEN_COLS + x;
    SCREEN[offset] = view_tile_char[tile];
    COLOURS[offset] = view_tile_colour[tile];
}

void view_init(void)
{
    unsigned char i, slot;
    const char* name = far_backend_name();

    bordercolor(COLOR_BLACK);
    bgcolor(COLOR_BLACK);
    cursor(0);
    screen_init();      /* also clears the screen */

    for (i = 0; i < TILE_COUNT; ++i)
        view_tile_char[i] = SCREEN_FIRST_GLYPH + tile_glyph[i];
    for (i = 0; i < CACHE_ROWS; ++i)
        cache_line[i] = cache[i];
    for (i = 0, slot = 0; ; ++i)
    {
        slot_table[i] = slot;
        if (++slot == CACHE_ROWS)
            slot = 0;
        if (i == 255)
            break;
    }
    for (i = 0; i < 128; ++i)
    {
        number_hundreds[i] = i < 25 ? ' ' : '0' + i / 25;
        number_hundreds_value[i] = i / 25 * 100;
    }
    for (i = 0; i < 100; ++i)
    {
        number_tens[i] = '0' + i / 10;
        number_tens_lead[i] = i < 10 ? ' ' : '0' + i / 10;
        number_ones[i] = '0' + i % 10;
    }
    for (i = 0; i < TILE_COUNT; ++i)
    {
        name = glyph_name[tile_glyph[i]];
        for (slot = 0; slot < 8; ++slot)
            tile_text[i][slot] = to_screen(*name ? *name++ : ' ');
    }
    name = far_backend_name();

    /* In the C64's and Plus/4's uppercase character set, a capital letter in a C string shows
     * as a graphics character, so the backend name ("REU") is shown in lowercase. */
    for (i = 0; i < sizeof(backend_name) - 1 && name[i]; ++i)
        backend_name[i] = tolower(name[i]);
    backend_name[i] = 0;

    view_invalidate();
}

void view_done(void)
{
    screen_done();
    textcolor(COLOR_LIGHTBLUE);
    bordercolor(COLOR_LIGHTBLUE);
    bgcolor(COLOR_BLUE);
    clrscr();
    cursor(1);
}

unsigned char* view_scratch(unsigned* size)
{
    view_invalidate();
    *size = sizeof(cache);
    return cache[0];
}

void view_invalidate(void)
{
    unsigned char i;

    for (i = 0; i < CACHE_ROWS; ++i)
        cache_lo[i] = cache_hi[i] = 0;
    drawn_valid = 0;
    status_dirty = 1;
#if VIEW_FINE_BITS
    ahead_ready = 0;
    ahead_left = NO_ROW;
    pred_from_top = NO_ROW;
#endif
}

/* ------------------------------------------------------------------------
 * The cache.
 */


#if VIEW_FINE_BITS == 0
/* The cache slot after `slot`, going down the map (the Plus/4's blit.s works this out itself). */
static unsigned char next_slot(unsigned char slot)
{
    return ++slot == CACHE_ROWS ? 0 : slot;
}
#endif

/* The map's width in CHUNKs. */
static unsigned char width_chunks(void)
{
    return (unsigned char)(world_width() >> CHUNK_SHIFT);
}

#if VIEW_FINE_BITS
/* The CHUNKs of columns a frame whose key is `left` needs: a to b - 1. */
static unsigned char first_chunk(unsigned left)
{
    return (unsigned char)(left >> CHUNK_SHIFT);
}

static unsigned char end_chunk(unsigned left)
{
    return (unsigned char)((left + VIEW_WIDTH + CHUNK - 1) >> CHUNK_SHIFT);
}

/* The CHUNKs there's time to read from far memory now without making the next step late (see
 * STEP_LINES) - 0 if now isn't the moment for a read (screen_far_read_lines), or there isn't time
 * for one. */
static unsigned char read_budget(void)
{
    unsigned char lines = screen_far_read_lines();
    unsigned char chunks;

    if (lines < STEP_LINES + READ_CHUNK_LINES)
        return 0;
    chunks = (lines - STEP_LINES) / READ_CHUNK_LINES;
    return chunks > READ_MAX ? READ_MAX : chunks;
}
#endif

/* Makes cache slot `slot` hold CHUNKs a to b - 1 of map row y, reading the ones it doesn't have -
 * all of them if `now`, else as many as there's time for this frame (Plus/4). Returns whether it
 * holds them all. A slot's columns stay one unbroken run, read out from either end of it; columns
 * that wouldn't join on, or another row, replace what was there. */
static unsigned char fill(unsigned char slot, unsigned y, unsigned char a, unsigned char b,
                          unsigned char now)
{
    unsigned char from, to;
#if VIEW_FINE_BITS
    unsigned char budget;
#endif

    if (cache_row[slot] != (unsigned char)y || a > cache_hi[slot] || b < cache_lo[slot])
    {
        cache_row[slot] = (unsigned char)y;
        cache_lo[slot] = cache_hi[slot] = a;
    }
    for (;;)
    {
        /* The missing columns on the right first: going right, the ones that come into view. */
        if (b > cache_hi[slot])
        {
            from = cache_hi[slot];
            to = b;
        }
        else if (a < cache_lo[slot])
        {
            from = a;
            to = cache_lo[slot];
        }
        else
            return 1;
#if VIEW_FINE_BITS
        if (!now)
        {
            budget = read_budget();
            if (budget == 0)
                return 0;
            /* Only as many as there's time for, next to the columns the slot has. */
            if (to - from > budget)
            {
                if (from == cache_hi[slot])
                    to = from + budget;
                else
                    from = to - budget;
            }
        }
#else
        (void)now;
#endif
        world_read((unsigned)from << CHUNK_SHIFT, y, cache_line[slot] + ((unsigned)from << CHUNK_SHIFT),
                   (unsigned)(to - from) << CHUNK_SHIFT);
        if (from == cache_hi[slot])
            cache_hi[slot] = to;
        else
            cache_lo[slot] = from;
    }
}

/* ------------------------------------------------------------------------
 * Frames.
 */

/* The top-left cell a frame has to be drawn from to show the view at (left, top): its "key".
 * Moves within the same key only scroll the screen; a new key needs a new frame. */
static unsigned key_left(unsigned left)
{
    return VIEW_CELL(left);
}

static unsigned key_top(unsigned top)
{
    /* See SCREEN_ROW_SHIFT: on the Plus/4, some fine scrolls need the frame a row further down. */
    return VIEW_CELL(top) + SCREEN_ROW_SHIFT(top & ((1 << VIEW_FINE_BITS) - 1));
}

/* Points blit.s at the tiles for each view row, for a frame whose key is (left, top). The rows
 * needn't all be in the cache yet: each slot's address is fixed. */
static void set_row_pointers(unsigned left, unsigned top)
{
#if VIEW_FINE_BITS
    blit_tiles = cache[0] + left;
    blit_slot = slot_of(top);
#else
    unsigned char row, slot = slot_of(top);
    const unsigned char* tiles;

    for (row = 0; row < VIEW_HEIGHT; ++row)
    {
        tiles = cache_line[slot] + left;
        view_row_lo[row] = (unsigned char)tiles;
        view_row_hi[row] = (unsigned char)((unsigned)tiles >> 8);
        slot = next_slot(slot);
    }
#endif
}

/* Writes text straight into the screen being drawn, padded with spaces to len characters. */
static void put_text(unsigned offset, const char* text, unsigned char len)
{
    unsigned char i;
    for (i = 0; i < len; ++i)
        screen_draw_chars[offset + i] = to_screen(*text ? *text++ : ' ');
}

/* Writes n (0-511) straight into the screen being drawn, right-aligned in three characters. */
static void put_number(unsigned offset, unsigned n)
{
    unsigned char* out = screen_draw_chars + offset;
    unsigned char hundreds = number_hundreds[(unsigned char)(n >> 2)];
    unsigned char rest = (unsigned char)(n - number_hundreds_value[(unsigned char)(n >> 2)]);

    out[0] = hundreds;
    out[1] = hundreds == ' ' ? number_tens_lead[rest] : number_tens[rest];
    out[2] = number_ones[rest];
}

/* The status bar's top line - "reu          x123 y 45 grass     m:map" - and its keys below. */
#define STATUS_LINE  (SCREEN_STATUS_ROW * SCREEN_COLS)

/* The screens the status bar's unchanging text has been written to since the map screen was last
 * set up (two on the Plus/4, which draws into each in turn). */
static unsigned char* status_text_on[2];

/* Writes the status bar, for the cell (x, y) under the cursor and its tile, into the screen
 * being drawn. Only the cell's place and tile change; the rest is written once to each screen,
 * as on the Plus/4 even this much C takes a good part of a frame. */
static void draw_status(unsigned x, unsigned y, unsigned char tile)
{
    if (screen_draw_chars != status_text_on[0] && screen_draw_chars != status_text_on[1])
    {
        put_text(STATUS_LINE, backend_name, 13);
        put_text(STATUS_LINE + 13, "x", 1);
        put_text(STATUS_LINE + 17, " y", 2);
        put_text(STATUS_LINE + 22, " ", 1);
        put_text(STATUS_LINE + 31, "  m:map", 9);
        put_text(STATUS_LINE + SCREEN_COLS, "t:town f:forest g:grass n:new q:quit", SCREEN_COLS);
        status_text_on[status_text_on[0] != 0] = screen_draw_chars;
    }
    put_number(STATUS_LINE + 14, x);
    put_number(STATUS_LINE + 19, y);
    memcpy(screen_draw_chars + STATUS_LINE + 23, tile_text[tile], 8);
}

/* Draws the status bar for a frame whose key is (left, top), into the hidden screen. The cursor's
 * row has to be in the cache. */
static void draw_status_for(unsigned left, unsigned top)
{
    draw_status(left + CURSOR_X, top + CURSOR_Y, cache_line[slot_of(top + CURSOR_Y)][left + CURSOR_X]);
}

/* Draws the cursor for a frame whose key is (left, top), into the hidden screen - after the map
 * row it's on - and notes the cell under it. */
static void draw_cursor(unsigned left, unsigned top)
{
    screen_draw_chars[CURSOR_OFFSET] = CURSOR_CHAR;
    screen_draw_colours[CURSOR_OFFSET] = screen_cursor_colour;
    cursor_x = left + CURSOR_X;
    cursor_y = top + CURSOR_Y;
}

#if VIEW_FINE_BITS
/* Moves pos by delta within 0..max, as main.c does - for following the view's path ahead. */
static unsigned step_pos(unsigned pos, int delta, unsigned max)
{
    if (delta < 0)
        return (unsigned)-delta > pos ? 0 : pos + delta;
    pos += delta;
    return pos > max ? max : pos;
}

/* Draws the rows of the frame ahead whose columns are in the cache, into the hidden screen, until
 * stop_line (blit.s). Returns whether there are none left to draw. */
static unsigned char draw_rows(unsigned char stop_line)
{
    unsigned char row, end, got;

    if (ahead_to_draw == 0)
        return 1;
    /* From the first row not drawn yet: going through the rows in C takes about a raster line
     * each, and this is called a few times a frame. */
    while (ahead_state[ahead_draw_from] == ROW_DRAWN)
        ++ahead_draw_from;
    row = ahead_draw_from;
    blit_chars = screen_draw_chars;
    blit_colours = screen_draw_colours;
    blit_stop_line = stop_line;
    while (row < VIEW_HEIGHT)
    {
        if (ahead_state[row] != ROW_TO_DRAW)
        {
            ++row;
            continue;
        }
        for (end = row + 1; end < VIEW_HEIGHT && ahead_state[end] == ROW_TO_DRAW; ++end)
            ;
        blit_end = end;
        got = view_blit_rows(row);
        ahead_to_draw -= got - row;
        while (row < got)
            ahead_state[row++] = ROW_DRAWN;
        if (got < end)
            return 0;
    }
    return 1;
}

/* The status bar for the frame ahead, if it isn't drawn yet and the cursor's row is in the cache
 * - while there are STATUS_LINES left in the frame. */
static void draw_ahead_status(void)
{
    if (!ahead_status && ahead_state[CURSOR_Y] != ROW_TO_READ && screen_time_for(STATUS_LINES))
    {
        draw_status_for(ahead_left, ahead_top);
        ahead_status = 1;
    }
}

/* While a read waits for the split (screen_set_copy_wait_work): the status bar, and rows until
 * WAIT_STOP_LINE, rather than waste the lower border. */
static void work_while_reading(void)
{
    draw_ahead_status();
    draw_rows(WAIT_STOP_LINE);
}

/* Reads columns the frame ahead needs that aren't in the cache yet, for the next row that needs
 * them, if it's the moment for a read. A frame only has time for one (screen_far_read_lines). */
static void read_row(void)
{
    unsigned char row = ahead_reads[ahead_read_next];
    unsigned char slot = ahead_slot + row;

    if (slot >= CACHE_ROWS)
        slot -= CACHE_ROWS;
    if (fill(slot, ahead_top + row, ahead_a, ahead_b, 0))
    {
        ahead_state[row] = ROW_TO_DRAW;
        ++ahead_to_draw;
        ++ahead_read_next;
    }
}

/* Spare time, with the frame ahead ready: widens the columns its rows have in the cache by up to
 * SPARE_CHUNKS either side, a row at a time, so that scrolling sideways finds them there rather
 * than having to wait for them. */
static void widen_rows(void)
{
    unsigned char a = ahead_a > SPARE_CHUNKS ? ahead_a - SPARE_CHUNKS : 0;
    unsigned char b = ahead_b + SPARE_CHUNKS;
    unsigned char slot;

    if (b > width_chunks())
        b = width_chunks();
    while (ahead_bg_row < VIEW_HEIGHT)
    {
        slot = ahead_slot + ahead_bg_row;
        if (slot >= CACHE_ROWS)
            slot -= CACHE_ROWS;
        if (!fill(slot, ahead_top + ahead_bg_row, a, b, 0))
            return;
        ++ahead_bg_row;
    }
}

/* key_top(top) is VIEW_CELL(top + KEY_TOP_OFFSET) on the Plus/4: SCREEN_ROW_SHIFT moves the key on
 * a row 5 pixels into a cell. */
#define KEY_TOP_OFFSET  3

/* How many moves of delta it takes from pos, kept between 0 and max, for VIEW_CELL(pos + off) to
 * change - 255 if it never does, at the map's edge. Counted rather than found by making the moves
 * one at a time, which in C took a good part of a frame. */
static unsigned char moves_to_next_cell(unsigned pos, int delta, unsigned max, unsigned char off)
{
    unsigned cell = VIEW_CELL(pos + off);
    unsigned char dist, step, moves;

    if (delta > 0)
    {
        if (VIEW_CELL(max + off) == cell)
            return 255;
        dist = (unsigned char)(VIEW_POS(cell + 1) - off - pos);     /* to the next cell's first */
        step = (unsigned char)delta;
    }
    else if (delta < 0)
    {
        if (cell == 0)
            return 255;
        dist = (unsigned char)(pos + off - VIEW_POS(cell) + 1);     /* to the last cell's last */
        step = (unsigned char)-delta;
    }
    else
        return 255;
    for (moves = 1; dist > step; ++moves)
        dist -= step;
    return moves;
}

/* Uses the time left over in this frame, after a move that only scrolled the screen, to get
 * ready for the next new frame: follows the view's latest move onward to the next key it'll
 * reach, reads ahead the columns of the map that frame needs, and draws as many of its rows into
 * the hidden screen as there's time for. */
static void prepare_ahead(unsigned left, unsigned top)
{
    int dx = (int)(left - last_left);
    int dy = (int)(top - last_top);
    unsigned max_left, max_top;
    unsigned next_left, next_top;
    unsigned char i, slot, moves, a, b, row;

    if (dx == 0 && dy == 0)
        return;

    if (dx == pred_dx && dy == pred_dy && drawn_left == pred_from_left && drawn_top == pred_from_top)
    {
        next_left = pred_left;
        next_top = pred_top;
    }
    else
    {
        /* The next key along the way the view is going: where it'll be after the fewest moves that
         * take either edge into another cell. */
        max_left = VIEW_POS(world_width() - VIEW_WIDTH);
        max_top = VIEW_POS(world_height() - VIEW_HEIGHT);
        moves = moves_to_next_cell(left, dx, max_left, 0);
        i = moves_to_next_cell(top, dy, max_top, KEY_TOP_OFFSET);
        if (i < moves)
            moves = i;
        if (moves == 255)
            return;                         /* at the map's edge: nowhere new to go */
        next_left = key_left(step_pos(left, dx * moves, max_left));
        next_top = key_top(step_pos(top, dy * moves, max_top));
        pred_dx = dx;
        pred_dy = dy;
        pred_from_left = drawn_left;
        pred_from_top = drawn_top;
        pred_left = next_left;
        pred_top = next_top;
    }
    if (next_left == drawn_left && next_top == drawn_top)
        return;                             /* at the map's edge: nowhere new to go */

    if (next_left != ahead_left || next_top != ahead_top)
    {
        /* A new frame to get ready: which of its rows have their columns in the cache already. */
        ahead_left = next_left;
        ahead_top = next_top;
        ahead_ready = 0;
        ahead_bg_row = 0;
        ahead_slot = slot = slot_of(next_top);
        ahead_a = a = first_chunk(next_left);
        ahead_b = b = end_chunk(next_left);
        ahead_to_draw = ahead_read_count = ahead_read_next = ahead_draw_from = 0;
        ahead_status = 0;
        row = (unsigned char)next_top;
        for (i = 0; i < VIEW_HEIGHT; ++i, ++row)
        {
            if (cache_row[slot] == row && cache_lo[slot] <= a && b <= cache_hi[slot])
            {
                ahead_state[i] = ROW_TO_DRAW;
                ++ahead_to_draw;
            }
            else
            {
                ahead_state[i] = ROW_TO_READ;
                ahead_reads[ahead_read_count++] = i;
            }
            if (++slot == CACHE_ROWS)
                slot = 0;
        }
        set_row_pointers(ahead_left, ahead_top);
    }
    if (ahead_ready)
    {
        widen_rows();
        return;
    }

    /* Columns to read first - the row coming into view, going up or down - as a read can only
     * start early in the frame. Then the status bar, once the cursor's row is in the cache: it
     * isn't on the map's rows, so it needn't wait for them, and left until last it often found too
     * little of the frame left. Then the rows whose columns are in the cache, as many as there's
     * time for. */
    if (ahead_read_next < ahead_read_count)
    {
        screen_set_copy_wait_work(work_while_reading);
        read_row();
        screen_set_copy_wait_work(0);
    }
    draw_ahead_status();
    if (!draw_rows(ROWS_STOP_LINE) || ahead_read_next < ahead_read_count || !ahead_status)
        return;

    /* Then the cursor, on its row, and the frame is ready. */
    draw_cursor(ahead_left, ahead_top);
    ahead_ready = 1;
}
#endif

void view_draw(unsigned left, unsigned top)
{
    unsigned char row, slot, a, b;
    unsigned char fine_x = left & ((1 << VIEW_FINE_BITS) - 1);
    unsigned char fine_y = top & ((1 << VIEW_FINE_BITS) - 1);
    unsigned cell_left = key_left(left);
    unsigned cell_top = key_top(top);

    /* Still the same frame: on the Plus/4, scroll it, and use the rest of the frame to get the
     * next one ready. */
    if (drawn_valid && !status_dirty && cell_left == drawn_left && cell_top == drawn_top)
    {
        screen_scroll(fine_x, fine_y);
#if VIEW_FINE_BITS
        prepare_ahead(left, top);
#endif
        last_left = left;
        last_top = top;
        return;
    }

    if (status_dirty)
    {
        /* Back from an overview or a cleared screen: the map screen, with the status bar's
         * colours set. */
        screen_map();
        memset(screen_draw_colours + STATUS_LINE, COLOR_GRAY3, 2 * SCREEN_COLS);
        status_text_on[0] = status_text_on[1] = 0;
        status_dirty = 0;
    }

    /* The frame - unless it was drawn ahead of time, ready for exactly this. */
#if VIEW_FINE_BITS
    if (ahead_ready && ahead_left == cell_left && ahead_top == cell_top)
    {
        cursor_x = cell_left + CURSOR_X;
        cursor_y = cell_top + CURSOR_Y;
    }
    else
#endif
    {
        /* Every row's columns, read now if need be: on the Plus/4 the view's and SPARE_CHUNKS
         * either side, on the C64 the whole row (see the cache). */
#if VIEW_FINE_BITS
        a = first_chunk(cell_left);
        a = a > SPARE_CHUNKS ? a - SPARE_CHUNKS : 0;
        b = end_chunk(cell_left) + SPARE_CHUNKS;
        if (b > width_chunks())
            b = width_chunks();
#else
        a = 0;
        b = width_chunks();
#endif
        slot = slot_of(cell_top);
        for (row = 0; row < VIEW_HEIGHT; ++row)
        {
            fill(slot, cell_top + row, a, b, 1);
            if (++slot == CACHE_ROWS)
                slot = 0;
        }
        set_row_pointers(cell_left, cell_top);
#if VIEW_FINE_BITS == 0
        view_blit();
#else
        blit_chars = screen_draw_chars;
        blit_colours = screen_draw_colours;
        blit_end = VIEW_HEIGHT;
        blit_stop_line = 255;               /* no stopping: lines only go up to 204 here */
        view_blit_rows(0);
#endif
        draw_status_for(cell_left, cell_top);
        draw_cursor(cell_left, cell_top);
    }

    /* Show it - and, on the Plus/4, the hidden screen is now the other one, so nothing is drawn
     * ahead in it yet. */
    screen_present(fine_x, fine_y);
#if VIEW_FINE_BITS
    ahead_ready = 0;
    ahead_left = NO_ROW;                    /* the hidden screen is now the other one */
#endif
    drawn_left = cell_left;
    drawn_top = cell_top;
    drawn_valid = 1;
#if VIEW_FINE_BITS
    /* screen_present has waited for the switch at the frame's end, leaving the frame after it
     * free: start on the next frame too. */
    prepare_ahead(left, top);
#endif
    last_left = left;
    last_top = top;
}

unsigned view_cursor_x(void)
{
    return cursor_x;
}

unsigned view_cursor_y(void)
{
    return cursor_y;
}

void view_overview_begin(const char* title)
{
    unsigned char x;

    screen_text();
    clrscr();
    textcolor(COLOR_GRAY3);
    cputsxy(0, 0, title);
    gotoxy(0, 1);
    cprintf("%ux%u cells in %s, %lu bytes", world_width(), world_height(), backend_name, far_size());

    for (x = 0; x < SCREEN_COLS; ++x)
        overview_x[x] = (unsigned)((unsigned long)x * world_width() / SCREEN_COLS);
}

unsigned view_overview_step(void)
{
    return world_height() / OVERVIEW_ROWS;
}

void view_overview_row(unsigned y, const unsigned char* tiles)
{
    unsigned char x;
    unsigned char screen_y;

    if (y % view_overview_step() != 0)
        return;
    screen_y = OVERVIEW_TOP + y / view_overview_step();
    for (x = 0; x < SCREEN_COLS; ++x)
        put_tile(x, screen_y, tiles[overview_x[x]]);
}

void view_overview_mark(unsigned x, unsigned y)
{
    unsigned offset = (OVERVIEW_TOP + y / view_overview_step()) * SCREEN_COLS
        + (unsigned)((unsigned long)x * SCREEN_COLS / world_width());
    SCREEN[offset] = CURSOR_CHAR;
    COLOURS[offset] = screen_cursor_colour;
}

void view_progress(unsigned char percent)
{
    static unsigned char flash;

    textcolor(COLOR_GRAY3);
    gotoxy(0, OVERVIEW_TOP + OVERVIEW_ROWS + 1);
    cprintf("%3u%%", percent);
    /* With the display off the border is the whole screen, so a gentle pulse. */
    flash ^= 1;
    bordercolor(flash ? screen_progress_colour : COLOR_BLACK);
}

void view_generating(unsigned char on)
{
    screen_blank(on);
    if (!on)
        bordercolor(COLOR_BLACK);
}

/*
 * The edit loop, screen layout and key bindings for Nano128.
 *
 * Commodore keyboards don't have most of the Ctrl+letter combos PC
 * keyboards do - Ctrl only pairs with a handful of keys (mostly the
 * colour codes on the number row), so nano's ^O/^W-style shortcuts
 * aren't available to us and the C128 function keys stand in for
 * those instead. Exit is the exception: Ctrl+X does work, since Ctrl
 * masks the key to its low 5 bits the same way a real terminal's
 * Ctrl does (Ctrl+X = 'X' & 0x1F = 24) - the same mechanism cc65's
 * own docs use for Ctrl+[ as Esc on the C64. That happens to match
 * nano's own ^X for Exit exactly. The shortcut bar at the bottom of
 * the screen documents the full mapping the way nano's own does:
 *
 *   F1 Help   F2 Save   F3 Search   F4 FindNext   F5 CutLine   F6 UnCut
 *   F7 PgUp   F8 PgDn   ^X Exit     Stop Open     Home BOL
 *
 * Everything else - arrow keys, Enter, Backspace (DEL) and typing -
 * works the way it would in any screen editor.
 */
#include <conio.h>
#include <cbm.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include "editor.h"
#include "buffer.h"
#include "screen.h"
#include "input.h"
#include "fileio.h"
#include "vblank.h"

#define TEXT_TOP      1
#define TEXT_ROWS     (SCREEN_ROWS - 4)
#define STATUS_ROW    (TEXT_TOP + TEXT_ROWS)
#define SHORTCUT_ROW  (STATUS_ROW + 1)

#define NANO_FILENAME_LEN 16
#define SEARCH_MAX         40

/* Ctrl+X: Commodore's Ctrl masks a key to its low 5 bits, same as a
 * real ASCII terminal (confirmed by cc65's own docs, which use Ctrl+[
 * for Esc on the C64 the same way: '[' & 0x1F == 27). */
#define CH_CTRL_X 24

/* With screen.c's CH_FONT_LOWER switch active, an unshifted letter key
 * sends PETSCII 65-90 (shown as lowercase) and Shift+letter sends
 * PETSCII 193-218 (shown as uppercase) - the mirror image of the
 * default uppercase/graphics charset. Both ranges must be accepted as
 * typed text, or Shift+letter would be silently dropped. */
#define SHIFTED_LETTER_MIN 193
#define SHIFTED_LETTER_MAX 218

#define REDRAW_NONE 0
#define REDRAW_LINE 1
#define REDRAW_PAGE 2

static unsigned int cur_line;
static unsigned char cur_col;
static unsigned int top_line;
static unsigned char left_col;

static char filename[NANO_FILENAME_LEN + 1];
static char search_text[SEARCH_MAX + 1];
static char cutline[BUF_MAX_LINE_LEN + 1];
static unsigned char has_cut;

/* ---- drawing ----------------------------------------------------- */

static void put_n(const char *s, unsigned char n)
{
    while (n--)
        cputc(*s++);
}

static void draw_line(unsigned int line)
{
    unsigned char row = TEXT_TOP + (unsigned char)(line - top_line);
    const char *text = buffer_line_text(line);
    unsigned char len = buffer_line_len(line);
    unsigned char visible;

    cclearxy(0, row, SCREEN_COLS);
    if (len > left_col)
    {
        visible = len - left_col;
        if (visible > SCREEN_COLS)
            visible = SCREEN_COLS;
        gotoxy(0, row);
        put_n(text + left_col, visible);
    }
}

static void draw_text_area(void)
{
    unsigned char row;
    unsigned int line;
    unsigned int count = buffer_line_count();

    for (row = 0; row < TEXT_ROWS; row++)
    {
        line = top_line + row;
        if (line < count)
            draw_line(line);
        else
            cclearxy(0, TEXT_TOP + row, SCREEN_COLS);
    }
}

/* Builds the whole 80-column row (left-hand status, free-RAM readout
 * right-aligned, spaces padding the middle) and writes it in a single
 * cputsxy() call. draw_title() runs on every keystroke via refresh(),
 * so the previous cclearxy()-then-cputsxy() version blanked the whole
 * row and repainted it a moment later - visible as a flicker on every
 * key. Writing the finished row in one pass removes the blank gap
 * entirely; skipping the write altogether when nothing actually
 * changed (e.g. successive Ctrl+X/F-key presses that don't touch the
 * filename, modified flag, cursor position, or free RAM) removes the
 * rest. */
static void draw_title(void)
{
    static char last[SCREEN_COLS + 1];
    char buf[SCREEN_COLS + 1];
    char left[SCREEN_COLS + 1];
    char right[16];
    unsigned char left_len, right_len, pad, i;

    sprintf(left, "Nano128   %s%s   Line %u/%u  Col %u",
            filename[0] ? filename : "New Buffer",
            buffer_modified() ? " (Modified)" : "",
            cur_line + 1, buffer_line_count(), (unsigned int)cur_col + 1);
    sprintf(right, "%uB free", _heapmemavail());

    left_len = (unsigned char)strlen(left);
    right_len = (unsigned char)strlen(right);
    if ((unsigned int)left_len + right_len + 2 > SCREEN_COLS)
        left_len = SCREEN_COLS - right_len - 2;

    memcpy(buf, left, left_len);
    pad = SCREEN_COLS - right_len;
    for (i = left_len; i < pad; i++)
        buf[i] = ' ';
    memcpy(buf + pad, right, right_len);
    buf[SCREEN_COLS] = '\0';

    if (memcmp(buf, last, SCREEN_COLS) == 0)
        return;
    memcpy(last, buf, SCREEN_COLS + 1);

    revers(1);
    cputsxy(0, 0, buf);
    revers(0);
}

static void draw_shortcuts(void)
{
    revers(1);
    cclearxy(0, SHORTCUT_ROW, SCREEN_COLS);
    cputsxy(0, SHORTCUT_ROW,
        "F1 Help      F2 Save      F3 Search    F4 FindNext  F5 CutLine   F6 UnCut");
    cclearxy(0, SHORTCUT_ROW + 1, SCREEN_COLS);
    cputsxy(0, SHORTCUT_ROW + 1,
        "F7 PgUp      F8 PgDn      ^X Exit      Stop Open    Home BOL");
    revers(0);
}

static void clear_status(void)
{
    cclearxy(0, STATUS_ROW, SCREEN_COLS);
}

static void status_msg(const char *msg)
{
    clear_status();
    cputsxy(0, STATUS_ROW, msg);
}

static void full_redraw(void)
{
	wait_vblank();
    draw_title();
    draw_text_area();
    clear_status();
    draw_shortcuts();
}

static void place_cursor(void)
{
    gotoxy(cur_col - left_col, TEXT_TOP + (unsigned char)(cur_line - top_line));
}

/* Keeps the cursor on screen by scrolling the viewport; reports
 * whether it actually had to scroll, so the caller knows whether a
 * full repaint of the text area is needed. */
static unsigned char scroll_to_cursor(void)
{
    unsigned int old_top = top_line;
    unsigned char old_left = left_col;

    if (cur_line < top_line)
        top_line = cur_line;
    else if (cur_line >= top_line + TEXT_ROWS)
        top_line = cur_line - TEXT_ROWS + 1;

    if (cur_col < left_col)
        left_col = cur_col;
    else if (cur_col >= (unsigned int)left_col + SCREEN_COLS)
        left_col = cur_col - SCREEN_COLS + 1;

    return (top_line != old_top) || (left_col != old_left);
}

static void refresh(unsigned char mode)
{
    unsigned char scrolled = scroll_to_cursor();

    if (scrolled || mode == REDRAW_PAGE)
        draw_text_area();
    else if (mode == REDRAW_LINE)
        draw_line(cur_line);

    draw_title();
    place_cursor();
}

/* ---- cursor movement ----------------------------------------------- */

static void clamp_col_to_line(void)
{
    unsigned char len = buffer_line_len(cur_line);
    if (cur_col > len)
        cur_col = len;
}

static unsigned char move_up(void)
{
    if (cur_line == 0)
        return REDRAW_NONE;
    cur_line--;
    clamp_col_to_line();
    return REDRAW_NONE;
}

static unsigned char move_down(void)
{
    if (cur_line + 1 >= buffer_line_count())
        return REDRAW_NONE;
    cur_line++;
    clamp_col_to_line();
    return REDRAW_NONE;
}

static unsigned char move_left(void)
{
    if (cur_col > 0)
    {
        cur_col--;
    }
    else if (cur_line > 0)
    {
        cur_line--;
        cur_col = buffer_line_len(cur_line);
    }
    return REDRAW_NONE;
}

static unsigned char move_right(void)
{
    unsigned char len = buffer_line_len(cur_line);

    if (cur_col < len)
    {
        cur_col++;
    }
    else if (cur_line + 1 < buffer_line_count())
    {
        cur_line++;
        cur_col = 0;
    }
    return REDRAW_NONE;
}

static unsigned char move_home(void)
{
    cur_col = 0;
    return REDRAW_NONE;
}

static unsigned char page_up(void)
{
    if (cur_line >= TEXT_ROWS)
        cur_line -= TEXT_ROWS;
    else
        cur_line = 0;
    clamp_col_to_line();
    return REDRAW_NONE;
}

static unsigned char page_down(void)
{
    unsigned int count = buffer_line_count();

    if (cur_line + TEXT_ROWS < count)
        cur_line += TEXT_ROWS;
    else
        cur_line = count - 1;
    clamp_col_to_line();
    return REDRAW_NONE;
}

/* ---- editing --------------------------------------------------------- */

static unsigned char do_enter(void)
{
    if (!buffer_split_line(cur_line, cur_col))
    {
        status_msg("Buffer full");
        return REDRAW_NONE;
    }
    cur_line++;
    cur_col = 0;
    return REDRAW_PAGE;
}

static unsigned char do_backspace(void)
{
    unsigned char prev_len;

    if (cur_col > 0)
    {
        buffer_delete_char(cur_line, cur_col - 1);
        cur_col--;
        return REDRAW_LINE;
    }

    if (cur_line == 0)
        return REDRAW_NONE;

    prev_len = buffer_line_len(cur_line - 1);
    if (!buffer_join_next_line(cur_line - 1))
    {
        status_msg("Line too long to join");
        return REDRAW_NONE;
    }
    cur_line--;
    cur_col = prev_len;
    return REDRAW_PAGE;
}

static unsigned char do_delete_forward(void)
{
    unsigned char len = buffer_line_len(cur_line);

    if (cur_col < len)
    {
        buffer_delete_char(cur_line, cur_col);
        return REDRAW_LINE;
    }

    if (cur_line + 1 >= buffer_line_count())
        return REDRAW_NONE;

    if (!buffer_join_next_line(cur_line))
    {
        status_msg("Line too long to join");
        return REDRAW_NONE;
    }
    return REDRAW_PAGE;
}

static unsigned char do_insert_char(unsigned char c)
{
    if (!buffer_insert_char(cur_line, cur_col, c))
    {
        status_msg("Line full");
        return REDRAW_NONE;
    }
    cur_col++;
    return REDRAW_LINE;
}

static unsigned char do_cut_line(void)
{
    unsigned int count;

    strncpy(cutline, buffer_line_text(cur_line), BUF_MAX_LINE_LEN);
    cutline[BUF_MAX_LINE_LEN] = '\0';
    has_cut = 1;

    if (buffer_line_count() > 1)
    {
        buffer_delete_line(cur_line);
        count = buffer_line_count();
        if (cur_line >= count)
            cur_line = count - 1;
    }
    else
    {
        buffer_delete_line(0);
        buffer_insert_line(0, "");
    }
    cur_col = 0;
    status_msg("Line cut");
    return REDRAW_PAGE;
}

static unsigned char do_uncut(void)
{
    if (!has_cut)
    {
        status_msg("Cut buffer is empty");
        return REDRAW_NONE;
    }
    if (!buffer_insert_line(cur_line, cutline))
    {
        status_msg("Buffer full");
        return REDRAW_NONE;
    }
    cur_line++;
    cur_col = 0;
    status_msg("Text uncut");
    return REDRAW_PAGE;
}

/* ---- search ------------------------------------------------------ */

static unsigned char find_in_line(const char *hay, const char *needle,
                                   unsigned char start, unsigned char *found_col)
{
    unsigned char hlen = (unsigned char)strlen(hay);
    unsigned char nlen = (unsigned char)strlen(needle);
    unsigned char i;

    if (nlen == 0 || nlen > hlen)
        return 0;

    for (i = start; (unsigned char)(i + nlen) <= hlen; i++)
    {
        if (memcmp(hay + i, needle, nlen) == 0)
        {
            *found_col = i;
            return 1;
        }
    }
    return 0;
}

/* Searches forward from (start_line, start_col), wrapping around the
 * end of the buffer once, nano-style. */
static unsigned char search_from(unsigned int start_line, unsigned char start_col)
{
    unsigned int count = buffer_line_count();
    unsigned int line = start_line;
    unsigned char col = start_col;
    unsigned char found_col;
    unsigned int scanned;

    for (scanned = 0; scanned <= count; scanned++)
    {
        if (find_in_line(buffer_line_text(line), search_text, col, &found_col))
        {
            cur_line = line;
            cur_col = found_col;
            return 1;
        }
        col = 0;
        line++;
        if (line >= count)
            line = 0;
    }
    return 0;
}

static unsigned char do_search(void)
{
    char buf[SEARCH_MAX + 1];

    status_msg("Search: ");
    if (!input_line(STATUS_ROW, 8, buf, SEARCH_MAX))
    {
        status_msg("Search cancelled");
        return REDRAW_NONE;
    }
    if (buf[0] == '\0')
    {
        clear_status();
        return REDRAW_NONE;
    }
    strcpy(search_text, buf);

    if (search_from(cur_line, cur_col + 1))
        clear_status();
    else
        status_msg("Not found");

    return REDRAW_NONE;
}

static unsigned char do_find_next(void)
{
    if (search_text[0] == '\0')
    {
        status_msg("No search text yet - press F3 first");
        return REDRAW_NONE;
    }
    if (search_from(cur_line, cur_col + 1))
        clear_status();
    else
        status_msg("Not found");
    return REDRAW_NONE;
}

/* ---- file commands ------------------------------------------------- */

static unsigned char do_save(void)
{
    char buf[NANO_FILENAME_LEN + 1];
    char msg[SCREEN_COLS + 1];

    strcpy(buf, filename);
    status_msg("Save as: ");
    if (!input_line(STATUS_ROW, 9, buf, NANO_FILENAME_LEN) || buf[0] == '\0')
    {
        status_msg("Save cancelled");
        return 0;
    }
    strcpy(filename, buf);

    if (fileio_save(filename))
    {
        sprintf(msg, "Wrote %u line(s) to %s", buffer_line_count(), filename);
        status_msg(msg);
        return 1;
    }

    sprintf(msg, "Error: could not write %s", filename);
    status_msg(msg);
    return 0;
}

/* Loads a different file without leaving the editor - like nano's ^R,
 * but replacing the whole buffer rather than inserting at the cursor.
 * Refuses to discard unsaved changes without confirmation first. */
static unsigned char do_open(void)
{
    char name[NANO_FILENAME_LEN + 1];
    char msg[SCREEN_COLS + 1];

    if (buffer_modified())
    {
        status_msg("Discard unsaved changes and open a file? (Y/N, Esc=Cancel)");
        if (input_confirm() != CONFIRM_YES)
        {
            clear_status();
            return REDRAW_NONE;
        }
    }

    status_msg("Open: ");
    if (!input_line(STATUS_ROW, 6, name, NANO_FILENAME_LEN) || name[0] == '\0')
    {
        status_msg("Open cancelled");
        return REDRAW_NONE;
    }

    if (!fileio_load(name))
    {
        sprintf(msg, "Error: could not open %s", name);
        status_msg(msg);
        return REDRAW_NONE;
    }

    strcpy(filename, name);
    cur_line = 0;
    cur_col = 0;
    top_line = 0;
    left_col = 0;
    has_cut = 0;

    sprintf(msg, "Read %u line(s) from %s", buffer_line_count(), filename);
    status_msg(msg);
    return REDRAW_PAGE;
}

/* Returns 1 if the editor should quit, 0 to keep editing. */
static unsigned char do_exit(void)
{
    unsigned char choice;

    if (!buffer_modified())
        return 1;

    status_msg("Save modified buffer? (Y/N, Esc=Cancel)");
    choice = input_confirm();

    switch (choice)
    {
        case CONFIRM_CANCEL:
            clear_status();
            return 0;
        case CONFIRM_NO:
            return 1;
        default:
            return do_save();
    }
}

static void do_help(void)
{
    clrscr();
    revers(1);
    cclearxy(0, 0, SCREEN_COLS);
    cputsxy(0, 0, " Nano128 Help ");
    revers(0);

    cputsxy(0, 2, "Movement:");
    cputsxy(2, 3, "Arrow keys    move the cursor");
    cputsxy(2, 4, "Home          jump to the start of the line");
    cputsxy(2, 5, "F7 / F8       page up / page down");

    cputsxy(0, 7, "Editing:");
    cputsxy(2, 8,  "Enter         split the line / start a new one");
    cputsxy(2, 9,  "Delete        backspace (joins with the line above at column 0)");
    cputsxy(2, 10, "Ins           delete the character under the cursor");
    cputsxy(2, 11, "F5 / F6       cut the current line / uncut (paste) it back");

    cputsxy(0, 13, "Files and search:");
    cputsxy(2, 14, "F2            save (Write Out)");
    cputsxy(2, 15, "F3 / F4       search / find next");
    cputsxy(2, 16, "Stop          open a different file (asks first if modified)");
    cputsxy(2, 17, "Ctrl+X        exit - offers to save first if modified");

    cputsxy(0, 23, "Press any key to return to the editor...");
    cgetc();
}

/* ---- entry point --------------------------------------------------- */

void editor_run(void)
{
    char name[NANO_FILENAME_LEN + 1];
    char msg[SCREEN_COLS + 1];
    unsigned char mode;
    unsigned char key;

    cur_line = 0;
    cur_col = 0;
    top_line = 0;
    left_col = 0;
    filename[0] = '\0';
    search_text[0] = '\0';
    has_cut = 0;

    cputsxy(0, STATUS_ROW, "File to open (Enter for a new file): ");
    if (input_line(STATUS_ROW, 37, name, NANO_FILENAME_LEN) && name[0])
    {
        strcpy(filename, name);
        if (fileio_load(filename))
        {
            sprintf(msg, "Read %u line(s) from %s", buffer_line_count(), filename);
        }
        else
        {
            buffer_free_all();
            buffer_insert_line(0, "");
            buffer_set_modified(0);
            sprintf(msg, "New file: %s", filename);
        }
    }
    else
    {
        strcpy(msg, "New Buffer");
    }

    full_redraw();
    status_msg(msg);
    place_cursor();

    for (;;)
    {
        key = cgetc();
        mode = REDRAW_NONE;

        switch (key)
        {
            case CH_CURS_UP:    mode = move_up();           break;
            case CH_CURS_DOWN:  mode = move_down();         break;
            case CH_CURS_LEFT:  mode = move_left();         break;
            case CH_CURS_RIGHT: mode = move_right();        break;
            case CH_HOME:       mode = move_home();         break;
            case CH_ENTER:      mode = do_enter();           break;
            case CH_DEL:        mode = do_backspace();       break;
            case CH_INS:        mode = do_delete_forward();  break;
            case CH_F1:         do_help(); full_redraw(); break;
            case CH_F2:         (void)do_save();             break;
            case CH_F3:         mode = do_search();          break;
            case CH_F4:         mode = do_find_next();       break;
            case CH_F5:         mode = do_cut_line();        break;
            case CH_F6:         mode = do_uncut();           break;
            case CH_F7:         mode = page_up();            break;
            case CH_F8:         mode = page_down();          break;

            case CH_STOP:
                mode = do_open();
                break;

            case CH_CTRL_X:
                if (do_exit())
                {
                    return;
                }
                break;

            default:
                if ((key >= 32 && key < 127) ||
                    (key >= SHIFTED_LETTER_MIN && key <= SHIFTED_LETTER_MAX))
                    mode = do_insert_char(key);
                break;
        }

        refresh(mode);
    }
}

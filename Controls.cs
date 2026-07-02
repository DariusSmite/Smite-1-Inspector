using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmiteGodLab
{
    // A single owner-drawn TOC row (chevron + label + accent bar painted in one Paint; no child controls).
    sealed class TocRow : Panel
    {
        public bool Hovered;
        public TocRow() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    // Flat checkbox: white square box with a black check mark when ticked (sharp, on-theme).
    class FlatCheck : CheckBox
    {
        public int BoxSize = 15;
        public FlatCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        public override Size GetPreferredSize(Size proposed)
        {
            var ts = TextRenderer.MeasureText(Text, Font);
            return new Size(BoxSize + BoxSize / 2 + ts.Width + 4, Math.Max(BoxSize, ts.Height));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int bs = BoxSize;
            var box = new Rectangle(0, (Height - bs) / 2, bs, bs);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, box);   // white box
            if (Checked)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.Black, Math.Max(2f, bs / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.Left + bs * 0.22f, box.Top + bs * 0.52f),
                        new PointF(box.Left + bs * 0.42f, box.Top + bs * 0.72f),
                        new PointF(box.Left + bs * 0.78f, box.Top + bs * 0.28f),
                    });   // black ✓
                g.SmoothingMode = SmoothingMode.None;
            }
            var tr = new Rectangle(box.Right + bs / 2, 0, Width - box.Right - bs / 2, Height);
            TextRenderer.DrawText(g, Text, Font, tr, ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // One row in a PlayerList (a search hit, a favorite, a friend, or a section header).
    class PlayerRow
    {
        public string Name = "", Id = "";
        public int Portal;
        public bool Priv, Deletable, Savable;                    // Deletable = trash glyph; Savable = ☆ add-to-favorites glyph
        public bool Header;                                      // a clickable section divider (Name = caption)
        public bool Collapsed;                                   // header collapse state (▶ vs ▼)
        public string Plat = "";                                 // short platform code, e.g. "STEAM"
        public Color PlatCol = Color.FromArgb(70, 70, 70);       // platform brand colour
        public string Status = "";                               // Friend List status text (e.g. "In Game"), drawn with a dot
        public Color StatusCol = Color.FromArgb(110, 110, 110);
        public string Extra = "";                                // secondary right-aligned text (e.g. last-login on the Friend List)
        public string Key = "";                                  // stable key for headers (collapse tracking)
        public DateTime LastLogin = DateTime.MinValue;           // for Friend List "last seen" sort
        public int StatusSort = 9;                               // for Friend List status sort (0 = in game … higher = offline)
        public string Avatar = "";                               // in-game avatar/icon URL (getplayer Avatar_URL) for the preview panel
        // Friend List live-poller scheduling (runtime only — never serialized to friendlist.json):
        public int Tier = 1;                                     // refresh priority: 0 god-select · 1 online/lobby · 2 in-game · 3 offline (backs off by days idle) · 4 unknown/error
        public DateTime NextDueUtc = DateTime.MinValue;          // when getplayerstatus is next eligible (MinValue = due now)
        public DateTime NextDetailUtc = DateTime.MinValue;       // when the slow getplayer (name/avatar/last-login) is next eligible
        public bool Polling;                                     // in-flight guard so a slow await spanning ticks can't double-schedule a row
        public int ErrBackoff;                                   // consecutive getplayerstatus failures → exponential backoff
        public static PlayerRow Section(string caption, string key = "") => new PlayerRow { Header = true, Name = caption, Key = key };
    }

    // Owner-drawn list used for search results, favorites and friends: a coloured platform
    // chip + the player name (+ "(private)") + an optional trash button (favorites only).
    class PlayerList : ListBox
    {
        readonly List<PlayerRow> _rows = new List<PlayerRow>();
        // shared row-background brushes (fixed colors) — cached so DrawRow doesn't allocate a SolidBrush per row per paint
        static readonly SolidBrush _brSel = new SolidBrush(Color.FromArgb(50, 50, 60)), _brHov = new SolidBrush(Color.FromArgb(34, 34, 42)),
            _brNorm = new SolidBrush(Color.FromArgb(20, 20, 20)), _brAccent = new SolidBrush(Color.FromArgb(193, 30, 31)), _brHdrBg = new SolidBrush(Color.FromArgb(13, 13, 13));
        Font _glyph, _chip, _hdr;
        int _hoverAction = -1;   // index of the row whose action glyph (trash/☆) the cursor is over → highlight it
        int _hoverRow = -1;      // index of the row the cursor is over (whole-row hover highlight)
        Bitmap _buf;             // off-screen double-buffer (WM_PAINT) — kills owner-draw flicker on live re-sorts

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate; [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        public event Action<PlayerRow> Activated;
        public event Action<PlayerRow> Deleted;
        public event Action<PlayerRow> Saved;          // ☆ clicked on a Savable row (add to favorites)
        public event Action<PlayerRow> HeaderClicked;  // a section header clicked (toggle collapse)

        public PlayerList()
        {
            // OwnerDrawFixed is load-bearing even though we paint in WM_PAINT (not WM_DRAWITEM): it makes ItemHeight
            // effective, which IndexFromPoint / GetItemRectangle / TopIndex all key off for hit-testing and scrolling.
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.FixedSingle;
            IntegralHeight = false;
            BackColor = Color.FromArgb(20, 20, 20);
            ForeColor = Color.White;
        }
        int Sc(int v) => v * DeviceDpi / 96;
        int TrashW => Sc(34);
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = Sc(30); }

        public IReadOnlyList<PlayerRow> Rows => _rows;
        public bool AutoSelectFirst = true;   // pickers default-select row 0 (keyboard nav); the Friend List opts out so no row looks "selected"
        public void SetRows(IEnumerable<PlayerRow> rows)
        {
            if (IsDisposed) return;   // a queued LiveSort BeginInvoke can fire during teardown — don't touch a dead handle
            // Preserve the selection BY IDENTITY across a re-sort, and update Items IN PLACE when the count is
            // unchanged (e.g. a live status re-sort) — a blanket Items.Clear()+re-add makes the native ListBox
            // blank then refill (the bulk of the refresh flicker).
            var prevSel = (SelectedIndex >= 0 && SelectedIndex < _rows.Count) ? _rows[SelectedIndex] : null;
            _rows.Clear(); _rows.AddRange(rows);
            BeginUpdate();
            if (Items.Count == _rows.Count)
                for (int i = 0; i < _rows.Count; i++) Items[i] = _rows[i].Name ?? "";
            else { Items.Clear(); foreach (var r in _rows) Items.Add(r.Name ?? ""); }   // ListBox.Items.Add(null) throws
            EndUpdate();
            if (_rows.Count == 0) { _hoverAction = _hoverRow = -1; Invalidate(); return; }
            int ns = prevSel != null ? _rows.IndexOf(prevSel) : -1;
            SelectedIndex = ns >= 0 ? ns : (AutoSelectFirst ? 0 : -1);          // don't fabricate a selection where there was none
            RefreshHover();   // rows moved under a possibly-stationary cursor → re-hit-test which glyph is hovered
            Invalidate();
        }
        // Repaint just one row (its status/name changed) instead of the whole control — flicker-free incremental update.
        public void UpdateRow(PlayerRow r)
        {
            if (!IsHandleCreated) return;
            int i = _rows.IndexOf(r);
            if (i < 0) return;
            var rc = GetItemRectangle(i);
            if (rc.IntersectsWith(ClientRectangle)) Invalidate(rc);
        }

        // Draws one row into g at bounds. Called from the double-buffered WM_PAINT (PaintBuffered), not WM_DRAWITEM.
        void DrawRow(Graphics g, int index, Rectangle bounds)
        {
            var r = _rows[index];

            if (r.Header)   // clickable section divider: ▼/▶ collapse arrow + accent caption
            {
                g.FillRectangle(_brHdrBg, bounds);
                if (_hdr == null) _hdr = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
                var arrow = new Rectangle(bounds.Left + Sc(6), bounds.Top, Sc(16), bounds.Height);
                TextRenderer.DrawText(g, r.Collapsed ? "▶" : "▼", Font, arrow, Color.FromArgb(193, 30, 31), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                var hrect = new Rectangle(bounds.Left + Sc(24), bounds.Top, bounds.Width - Sc(26), bounds.Height);
                TextRenderer.DrawText(g, r.Name, _hdr, hrect, Color.FromArgb(193, 30, 31), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            bool sel = index == SelectedIndex;
            bool hov = index == _hoverRow && !sel;   // whole-row hover highlight
            g.FillRectangle(sel ? _brSel : hov ? _brHov : _brNorm, bounds);
            if (sel) g.FillRectangle(_brAccent, bounds.Left, bounds.Top, Sc(3), bounds.Height);   // red accent bar = selected
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_chip == null) _chip = new Font(Font.FontFamily, Math.Max(6f, Font.Size - 1.5f), FontStyle.Bold);
            int pad = Sc(6);
            var chip = new Rectangle(bounds.Left + pad, bounds.Top + Sc(5), Sc(58), bounds.Height - Sc(10));
            using (var cb = new SolidBrush(r.PlatCol)) g.FillRectangle(cb, chip);
            TextRenderer.DrawText(g, r.Plat, _chip, chip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int nameX = chip.Right + Sc(10);
            int rEdge = bounds.Right;
            bool hasAction = r.Deletable || r.Savable;
            int rightLimit = rEdge - (hasAction ? TrashW : Sc(6));

            if (!string.IsNullOrEmpty(r.Status))   // Friend List: status dot + text, right-aligned
            {
                var sz = TextRenderer.MeasureText(r.Status, Font);
                int stX = rightLimit - sz.Width - Sc(4);
                int dotX = stX - Sc(15), dotY = bounds.Top + bounds.Height / 2 - Sc(4);
                using (var db = new SolidBrush(r.StatusCol)) g.FillEllipse(db, dotX, dotY, Sc(9), Sc(9));
                TextRenderer.DrawText(g, r.Status, Font, new Rectangle(stX, bounds.Top, sz.Width + Sc(6), bounds.Height), r.StatusCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                rightLimit = dotX - Sc(8);
            }
            if (!string.IsNullOrEmpty(r.Extra))   // e.g. last-login, dim, to the left of the status
            {
                var ez = TextRenderer.MeasureText(r.Extra, _chip);
                int exX = rightLimit - ez.Width - Sc(4);
                TextRenderer.DrawText(g, r.Extra, _chip, new Rectangle(exX, bounds.Top, ez.Width + Sc(6), bounds.Height), Color.FromArgb(120, 120, 120), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                rightLimit = exX - Sc(8);
            }

            var nameRect = new Rectangle(nameX, bounds.Top, rightLimit - nameX, bounds.Height);
            string nm = r.Name + (r.Priv ? "   (private)" : "");
            TextRenderer.DrawText(g, nm, Font, nameRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (hasAction)
            {
                if (_glyph == null) { try { _glyph = new Font("Segoe MDL2 Assets", Font.Size); } catch { _glyph = Font; } }
                // trash glyph drawn inline below (char-code form). original:"✕" : "";   // Segoe MDL2 trash, else a ✕
                var tr = new Rectangle(rEdge - TrashW, bounds.Top, TrashW, bounds.Height);
                bool hot = index == _hoverAction;   // cursor is over this row's action glyph → highlight it
                string glyph; Color gc;
                var hotRect = new Rectangle(tr.Left + Sc(2), tr.Top + Sc(4), tr.Width - Sc(4), tr.Height - Sc(8));
                if (r.Deletable)
                {
                    glyph = _glyph == Font ? "X" : ((char)0xE74D).ToString();                                  // trash
                    if (hot) RoundRect(g, hotRect, Sc(6), Color.FromArgb(110, 210, 70, 70), Color.FromArgb(200, 225, 95, 95));
                    gc = hot ? Color.FromArgb(255, 150, 150) : Color.FromArgb(205, 90, 90);
                }
                else
                {
                    glyph = _glyph == Font ? "+" : ((char)0xE734).ToString();                                  // star = add to favorites
                    if (hot) RoundRect(g, hotRect, Sc(6), Color.FromArgb(80, 230, 190, 60), Color.FromArgb(190, 235, 200, 80));
                    gc = hot ? Color.FromArgb(255, 215, 90) : Color.FromArgb(214, 170, 40);
                }
                TextRenderer.DrawText(g, glyph, _glyph, tr, gc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexFromPoint(e.Location);
            if (i < 0 || i >= _rows.Count) { base.OnMouseDown(e); return; }
            var r = _rows[i];
            if (r.Header) { HeaderClicked?.Invoke(r); return; }   // toggle collapse
            SelectedIndex = i; _hoverRow = i;
            Invalidate(); Update();   // show the selection immediately — don't wait for the next mouse-move
            if ((r.Deletable || r.Savable) && e.X >= ClientSize.Width - TrashW)   // action glyph at the right edge
            { if (r.Deletable) Deleted?.Invoke(r); else Saved?.Invoke(r); return; }
            base.OnMouseDown(e);
            if (!string.IsNullOrEmpty(r.Id) && r.Id != "0") Activated?.Invoke(r);                   // hidden/no-id rows aren't loadable
        }

        // The row index whose action glyph (right edge) the point falls on, else -1.
        int ActionIndexAt(Point p)
        {
            int i = IndexFromPoint(p);
            if (i < 0 || i >= _rows.Count || _rows[i].Header) return -1;
            if (!(_rows[i].Deletable || _rows[i].Savable)) return -1;
            return p.X >= ClientSize.Width - TrashW ? i : -1;
        }
        // The non-header row index under the point, else -1 (for whole-row hover highlight).
        int RowAt(Point p)
        {
            int i = IndexFromPoint(p);
            return (i >= 0 && i < _rows.Count && !_rows[i].Header) ? i : -1;
        }
        bool Loadable(int i) => i >= 0 && i < _rows.Count && !string.IsNullOrEmpty(_rows[i].Id) && _rows[i].Id != "0";
        void SetHover(int row, int action)
        {
            if (row == _hoverRow && action == _hoverAction) return;
            _hoverRow = row; _hoverAction = action;
            Invalidate();   // buffered paint → a full repaint is flicker-free
        }
        // Re-evaluate hover from the CURRENT cursor (after a re-sort moves rows under a stationary cursor).
        public void RefreshHover()
        {
            int row = -1, act = -1;
            if (IsHandleCreated) { var p = PointToClient(Cursor.Position); if (ClientRectangle.Contains(p)) { row = RowAt(p); act = ActionIndexAt(p); } }
            SetHover(row, act);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int act = ActionIndexAt(e.Location), row = RowAt(e.Location);
            Cursor = (act >= 0 || Loadable(row)) ? Cursors.Hand : Cursors.Default;
            SetHover(row, act);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            SetHover(-1, -1);
        }

        static void RoundRect(Graphics g, Rectangle r, int rad, Color fill, Color border)
        {
            using var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad, rad, 180, 90);
            p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            p.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
            p.CloseFigure();
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (border.A > 0) using (var pen = new Pen(border)) g.DrawPath(pen, p);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }     // we paint the whole client in WM_PAINT
            if (m.Msg == WM_PAINT && PaintBuffered()) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);   // PaintBuffered returns false only if BeginPaint failed → let base validate (no repaint loop)
            // The native ListBox scrolls by bit-blitting existing pixels and invalidating only the exposed strip; that
            // WM_PAINT is low-priority and gets deferred while wheel/drag input keeps coming, so scrolling looks frozen
            // until input stops. Force the exposed strip to paint NOW.
            if (m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL || m.Msg == WM_KEYDOWN) Update();
        }
        // Double-buffered repaint: draw the visible rows to an off-screen bitmap, then blit (the DC is clipped to the
        // update region, so a scroll only repaints the exposed strip from the buffer — the native bit-blit already
        // shifted the rest correctly). This kills owner-draw flicker (a native OwnerDraw ListBox paints each item
        // straight to the screen DC, so a live re-sort visibly redraws row-by-row).
        bool PaintBuffered()
        {
            var hdc = BeginPaint(Handle, out var ps);
            if (hdc == IntPtr.Zero) return false;   // BeginPaint failed (GDI exhausted / dying window) — don't validate; let base try
            try
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w > 0 && h > 0)
                {
                    if (_buf == null || _buf.Width != w || _buf.Height != h) { _buf?.Dispose(); _buf = new Bitmap(w, h); }
                    using (var g = Graphics.FromImage(_buf))
                    {
                        g.Clear(BackColor);
                        int top = Math.Max(0, TopIndex), ih = Math.Max(1, ItemHeight);
                        for (int i = top; i < _rows.Count; i++)
                        {
                            int y = (i - top) * ih;
                            if (y >= h) break;
                            try { DrawRow(g, i, new Rectangle(0, y, w, ih)); } catch { }   // one bad row must not drop the whole frame
                        }
                    }
                    using (var screen = Graphics.FromHdc(hdc)) screen.DrawImageUnscaled(_buf, 0, 0);
                }
            }
            catch { }
            finally { EndPaint(Handle, ref ps); }
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (SelectedIndex >= 0 && SelectedIndex < _rows.Count)
            {
                var r = _rows[SelectedIndex];
                if (!r.Header)
                {
                    if (e.KeyCode == Keys.Enter && !string.IsNullOrEmpty(r.Id) && r.Id != "0") { e.SuppressKeyPress = true; Activated?.Invoke(r); return; }
                    if (e.KeyCode == Keys.Delete && r.Deletable) { e.SuppressKeyPress = true; Deleted?.Invoke(r); return; }
                }
            }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (_glyph != null && _glyph != Font) _glyph.Dispose(); _chip?.Dispose(); _hdr?.Dispose(); _buf?.Dispose(); }
            base.Dispose(disposing);
        }
    }
    // A flicker-free panel (double-buffered) — used for the conversation list + rows so in-place updates don't repaint-flash.
    sealed class BufPanel : Panel { public BufPanel() { DoubleBuffered = true; } }
}

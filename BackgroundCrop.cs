using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageWidget
{
    internal sealed class CropSelection
    {
        readonly Size source, output;
        PointF center;
        double zoom = 1;
        public CropSelection(Size source, Size output)
        {
            if (source.Width <= 0 || source.Height <= 0 || output.Width <= 0 || output.Height <= 0) throw new ArgumentOutOfRangeException("source");
            this.source = source; this.output = output; center = new PointF(source.Width / 2f, source.Height / 2f);
        }
        public PointF Center { get { return center; } }
        public double Zoom
        {
            get { return zoom; }
            set {
                if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentOutOfRangeException("value");
                zoom = Math.Max(1, Math.Min(4, value)); MoveTo(center);
            }
        }
        public RectangleF Bounds
        {
            get {
                double aspect = (double)output.Width / output.Height;
                float width = (float)(Math.Min(source.Width, source.Height * aspect) / zoom), height = (float)(width / aspect);
                return new RectangleF(center.X - width / 2, center.Y - height / 2, width, height);
            }
        }
        public void MoveTo(PointF value)
        {
            if (Single.IsNaN(value.X) || Single.IsInfinity(value.X) || Single.IsNaN(value.Y) || Single.IsInfinity(value.Y)) throw new ArgumentOutOfRangeException("value");
            var bounds = Bounds;
            center = new PointF(Math.Max(bounds.Width / 2, Math.Min(source.Width - bounds.Width / 2, value.X)), Math.Max(bounds.Height / 2, Math.Min(source.Height - bounds.Height / 2, value.Y)));
        }
        public Bitmap Render(Bitmap image)
        {
            if (image.Size != source) throw new ArgumentException("The crop belongs to a different image.");
            var result = new Bitmap(output.Width, output.Height);
            try {
                using (var g = Graphics.FromImage(result)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(image, new Rectangle(0, 0, output.Width, output.Height), Bounds, GraphicsUnit.Pixel);
                }
                return result;
            } catch { result.Dispose(); throw; }
        }
    }
    internal sealed class CropPreview : Control
    {
        readonly Bitmap image;
        readonly CropSelection crop;
        bool dragging;
        PointF dragOffset;
        public event MouseEventHandler ZoomWheel;
        public CropPreview(Bitmap image, CropSelection crop)
        {
            this.image = image; this.crop = crop;
            DoubleBuffered = true; BackColor = Color.FromArgb(8, 12, 19); Cursor = Cursors.SizeAll; TabStop = true;
            AccessibleName = "Background crop preview"; AccessibleRole = AccessibleRole.Graphic;
            AccessibleDescription = "Drag the white frame or use the arrow keys to choose the visible area. Use the zoom slider or mouse wheel to zoom.";
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }
        RectangleF ImageArea
        {
            get {
                float scale = Math.Min(Math.Max(1, ClientSize.Width - 24) / (float)image.Width, Math.Max(1, ClientSize.Height - 24) / (float)image.Height);
                float width = image.Width * scale, height = image.Height * scale;
                return new RectangleF((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
            }
        }
        RectangleF Frame
        {
            get {
                var area = ImageArea; var selection = crop.Bounds; float scale = area.Width / image.Width;
                return new RectangleF(area.X + selection.X * scale, area.Y + selection.Y * scale, selection.Width * scale, selection.Height * scale);
            }
        }
        PointF ImagePoint(Point point)
        {
            var area = ImageArea; float scale = image.Width / area.Width;
            return new PointF((point.X - area.X) * scale, (point.Y - area.Y) * scale);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var area = ImageArea; var frame = Frame;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; e.Graphics.DrawImage(image, area);
            using (var outside = new Region(area))
            using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) {
                outside.Exclude(frame); e.Graphics.FillRegion(shade, outside);
            }
            using (var pen = new Pen(Color.White, 2)) e.Graphics.DrawRectangle(pen, frame.X, frame.Y, frame.Width, frame.Height);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(2, 2, Width - 4, Height - 4), Color.White, BackColor);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !ImageArea.Contains(e.Location)) return;
            Focus(); var point = ImagePoint(e.Location);
            if (Frame.Contains(e.Location)) dragOffset = new PointF(point.X - crop.Center.X, point.Y - crop.Center.Y);
            else { crop.MoveTo(point); dragOffset = PointF.Empty; }
            dragging = true; Capture = true; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!dragging) return;
            var point = ImagePoint(e.Location); crop.MoveTo(new PointF(point.X - dragOffset.X, point.Y - dragOffset.Y)); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) dragging = false; }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (ZoomWheel != null) ZoomWheel(this, e); }
        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); float step = Math.Max(1, crop.Bounds.Width / 100) * (e.Shift ? 10 : 1);
            var point = crop.Center;
            if (e.KeyCode == Keys.Left) point.X -= step;
            else if (e.KeyCode == Keys.Right) point.X += step;
            else if (e.KeyCode == Keys.Up) point.Y -= step;
            else if (e.KeyCode == Keys.Down) point.Y += step;
            else return;
            crop.MoveTo(point); e.Handled = true; Invalidate();
        }
    }
    internal sealed class BackgroundCropDialog : Form
    {
        public Bitmap SelectedImage { get; private set; }
        public BackgroundCropDialog(Bitmap image, Size windowSize)
        {
            Text = "Position background"; ClientSize = new Size(620, 430); MinimumSize = new Size(460, 340);
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f); BackColor = Color.FromArgb(14, 19, 29); ForeColor = Color.FromArgb(235, 240, 248);
            var crop = new CropSelection(image.Size, windowSize);
            var instruction = new Label { Text = "Move the white frame to choose the visible area.", Bounds = new Rectangle(16, 14, 588, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var preview = new CropPreview(image, crop) { Bounds = new Rectangle(16, 44, 588, 282), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, TabIndex = 0 };
            var caption = new Label { Text = "Zoom", Bounds = new Rectangle(16, 340, 50, 24), Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            var zoom = new TrackBar { Minimum = 100, Maximum = 400, Value = 100, TickFrequency = 100, SmallChange = 10, LargeChange = 25, Bounds = new Rectangle(66, 334, 324, 42), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, AccessibleName = "Background zoom", TabIndex = 1 };
            var percent = new Label { Text = "100%", Bounds = new Rectangle(396, 340, 64, 24), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            var reset = new Button { Text = "Reset", Bounds = new Rectangle(474, 334, 130, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, TabIndex = 2 };
            var use = new Button { Text = "Use image", Bounds = new Rectangle(364, 384, 114, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, TabIndex = 3 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(490, 384, 114, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, TabIndex = 4 };
            Controls.AddRange(new Control[] { instruction, preview, caption, zoom, percent, reset, use, cancel });
            AcceptButton = use; CancelButton = cancel;
            zoom.ValueChanged += delegate { crop.Zoom = zoom.Value / 100.0; percent.Text = zoom.Value + "%"; preview.Invalidate(); };
            preview.ZoomWheel += delegate(object sender, MouseEventArgs e) { zoom.Value = Math.Max(zoom.Minimum, Math.Min(zoom.Maximum, zoom.Value + Math.Sign(e.Delta) * 25)); };
            reset.Click += delegate { zoom.Value = 100; crop.MoveTo(new PointF(image.Width / 2f, image.Height / 2f)); preview.Invalidate(); };
            use.Click += delegate { SelectedImage = crop.Render(image); DialogResult = DialogResult.OK; Close(); };
        }
    }
}

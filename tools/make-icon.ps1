# Draws the app icon (dark rounded square, orange frame, the three white tiles from the tray icon)
# and writes assets\komorebi-tray.ico: 32-bit DIB entries up to 64 px (readable by every Windows API) plus a 256 px PNG.
param([string]$Out = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'assets\komorebi-tray.ico'))
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.IO; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging;
public static class IconMaker {
  static GraphicsPath Round(RectangleF r, float rad) {
    var p = new GraphicsPath(); float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure(); return p;
  }
  public static Bitmap Draw(int size) {
    int big = Math.Max(size * 4, 512);
    var hi = new Bitmap(big, big, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(hi)) {
      g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
      float u = big / 256f;
      bool tiny = size <= 24;   // at 16-24 px the frame would be mush: tiles fill the square instead
      using (var bg = Round(new RectangleF(4*u, 4*u, 248*u, 248*u), 52*u))
      using (var b = new SolidBrush(Color.FromArgb(28, 28, 30))) g.FillPath(b, bg);
      if (!tiny) using (var bg = Round(new RectangleF(12*u, 12*u, 232*u, 232*u), 44*u))
      using (var pen = new Pen(Color.FromArgb(192, 69, 23), 14*u)) g.DrawPath(pen, bg);
      float m = (tiny ? 44 : 58) * u, gap = 16 * u, rad = 18 * u, mid = big / 2f;
      using (var w = new SolidBrush(Color.White)) {
        Action<float,float,float,float> tile = (x0,y0,x1,y1) => { using (var p = Round(new RectangleF(x0,y0,x1-x0,y1-y0), rad)) g.FillPath(w, p); };
        tile(m, m, mid - gap/2, big - m);
        tile(mid + gap/2, m, big - m, mid - gap/2);
        tile(mid + gap/2, mid + gap/2, big - m, big - m);
      }
    }
    var final = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(final)) {
      g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(hi, new Rectangle(0, 0, size, size));
    }
    hi.Dispose(); return final;
  }
  public static void Write(string path, int[] sizes) {
    var images = new byte[sizes.Length][];
    for (int i = 0; i < sizes.Length; i++) {
      int s = sizes[i]; var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
      // 256 px as PNG keeps the file small; Windows only picks it for large views (Icon(path) loads the 32 px DIB)
      if (s >= 256) { using (var bmp = Draw(s)) bmp.Save(ms, ImageFormat.Png); images[i] = ms.ToArray(); continue; }
      bw.Write(40); bw.Write(s); bw.Write(s * 2); bw.Write((short)1); bw.Write((short)32);
      bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
      using (var bmp = Draw(s))
        for (int y = s - 1; y >= 0; y--) for (int x = 0; x < s; x++) { var c = bmp.GetPixel(x, y); bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A); }
      int maskRow = ((s + 31) / 32) * 4; bw.Write(new byte[maskRow * s]);
      images[i] = ms.ToArray();
    }
    using (var f = new BinaryWriter(File.Create(path))) {
      f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
      int offset = 6 + 16 * sizes.Length;
      for (int i = 0; i < sizes.Length; i++) {
        int s = sizes[i];
        f.Write((byte)(s >= 256 ? 0 : s)); f.Write((byte)(s >= 256 ? 0 : s)); f.Write((byte)0); f.Write((byte)0);
        f.Write((short)1); f.Write((short)32); f.Write(images[i].Length); f.Write(offset); offset += images[i].Length;
      }
      foreach (var img in images) f.Write(img);
    }
  }
}
"@
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
[IconMaker]::Write($Out, [int[]](16, 20, 24, 32, 40, 48, 64, 256))
$png = [IO.Path]::ChangeExtension($Out, '.png')
$b = [IconMaker]::Draw(256); $b.Save($png, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
Write-Host "Wrote $Out and $png"

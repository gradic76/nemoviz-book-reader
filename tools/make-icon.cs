// tools/make-icon.cs -- builds NBR.ico from the brand artwork.
//
// Run it when the drawing changes; it writes nbr-book.ico plus a size ladder to
// look at. Compile with Roslyn's csc and System.Drawing:
//
//   csc -r:System.Drawing.dll -out:make-icon.exe make-icon.cs
//
// WHAT IS TAKEN FROM THE ART AND WHAT IS REDRAWN. The source is a JPEG and JPEG
// has chewed the geometry -- measured, 2261 distinct reds where the ring should
// have one. So the RING and the SLASH are redrawn at every size from ratios read
// off the original (circle 216 px, ring 24, slash 25, red #C4151C), and only the
// hand-drawn EYE is carried across, with the red removed and the mottle on the
// white snapped back. The slash width is 25/sqrt(2): 25 is the horizontal chord
// a row-scan measures through a bar lying at 45 degrees, not the bar itself.
//
// THE BOOK IS DRAWN HERE, and the one thing that makes it read as a book rather
// than a banner is that the top edge SAGS towards the spine. Put the spine higher
// than the outer corners and the shape becomes a ribbon; that was the first two
// attempts. Pages want to be about square, so the book is 0.80 wide by 0.50 tall
// with a sag of 0.38.
//
// THE SIZE THRESHOLDS ARE NOT TASTE, THEY ARE WHAT SURVIVES THE PIXELS:
//   16, 20, 24  the brand circle alone -- a book at 24 px is a blob
//   32          circle and book, no eye -- fine lashes at that size are noise
//   48 and up   all three
using System; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging; using System.IO;
class L {
  const string Src=@"D:\Player\nemoviz\oko zabrana prozirna kvadratna.jpg";
  static readonly Rectangle Art=new Rectangle(183,184,216,217);
  static readonly Color Red=Color.FromArgb(196,21,28);
  static readonly Color Ink=Color.FromArgb(255,26,26,28);
  const double ROuter=107.0/216.0, RRing=24.0/216.0;
  static readonly double WSlash=25.0/216.0/Math.Sqrt(2);
  static Bitmap eye;
  static void Prep(){ using(var s=new Bitmap(Src)) using(var c=s.Clone(Art,PixelFormat.Format32bppArgb)){
    eye=new Bitmap(c.Width,c.Height,PixelFormat.Format32bppArgb);
    for(int y=0;y<c.Height;y++) for(int x=0;x<c.Width;x++){ Color p=c.GetPixel(x,y);
      bool red=p.R>100&&p.R>p.G*3/2&&p.R>p.B*3/2; int lum=(p.R*30+p.G*59+p.B*11)/100;
      eye.SetPixel(x,y,(red||lum>236)?Color.White:Color.FromArgb(255,p.R,p.G,p.B)); } } }
  static void Circle(Graphics g,float ox,float oy,float D,bool withEye){
    float c=D/2f, ro=(float)(ROuter*D), ring=(float)(RRing*D), ri=ro-ring, sl=(float)(WSlash*D);
    var st0=g.Save(); g.TranslateTransform(ox,oy);
    g.FillEllipse(Brushes.White,c-ri,c-ri,ri*2,ri*2);
    if(withEye){ var cl=new GraphicsPath(); cl.AddEllipse(c-ri,c-ri,ri*2,ri*2); g.SetClip(cl);
      g.DrawImage(eye,0,0,D,D); g.ResetClip(); }
    using(var pen=new Pen(Red,ring)) g.DrawEllipse(pen,c-ro+ring/2,c-ro+ring/2,(ro-ring/2)*2,(ro-ring/2)*2);
    var st=g.Save(); g.TranslateTransform(c,c); g.RotateTransform(45);
    g.FillRectangle(new SolidBrush(Red),-ro,-sl/2f,ro*2,sl); g.Restore(st); g.Restore(st0); }
  static void OpenBook(Graphics g,float x0,float y0,float w,float h,float stroke,float sag,bool lines){
    float cx=x0+w/2f;
    float topOuter=y0, topSpine=y0+h*sag, botOuter=y0+h*(1f-sag), botSpine=y0+h;
    for(int side=0;side<2;side++){
      float sx=side==0?x0:x0+w; float dir=side==0?-1f:1f;
      var p=new GraphicsPath();
      p.AddBezier(sx,topOuter, cx+(sx-cx)*0.72f,topOuter+h*sag*0.30f, cx+(sx-cx)*0.34f,topSpine-h*sag*0.12f, cx,topSpine);
      p.AddLine(cx,topSpine, cx,botSpine);
      p.AddBezier(cx,botSpine, cx+(sx-cx)*0.34f,botSpine-h*sag*0.12f, cx+(sx-cx)*0.72f,botOuter+h*sag*0.30f, sx,botOuter);
      p.CloseFigure();
      g.FillPath(Brushes.White,p);
      using(var pen=new Pen(Ink,stroke){LineJoin=LineJoin.Round}) g.DrawPath(pen,p);
      if(lines) using(var pen=new Pen(Color.FromArgb(160,26,26,28),Math.Max(1f,stroke*0.55f)))
        for(int i=1;i<=4;i++){ float t=i/5f;
          g.DrawLine(pen, cx+dir*w*0.07f, topSpine+(botSpine-topSpine)*t, sx-dir*w*0.07f, topOuter+(botOuter-topOuter)*t); }
    }
    using(var pen=new Pen(Ink,stroke){LineJoin=LineJoin.Round}) g.DrawLine(pen,cx,topSpine,cx,botSpine); }

  static Bitmap R(int S){
    var bm=new Bitmap(S,S,PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(bm)){
      g.SmoothingMode=SmoothingMode.AntiAlias; g.InterpolationMode=InterpolationMode.HighQualityBicubic;
      g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
      if(S<32){ Circle(g,0,0,S,false); return bm; }        // 16-24: the brand alone -- a book at 24 px is a blob
      OpenBook(g,S*0.10f,S*0.485f,S*0.80f,S*0.50f,Math.Max(1f,S*0.024f),0.38f,S>=48);
      Circle(g,S*0.25f,S*0.005f,S*0.50f,S>=48);
    }
    return bm; }

  [STAThread] static void Main(){
    Prep();
    int[] sizes={16,20,24,32,48,64,128,256};
    using(var sh=new Bitmap(1000,540,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(sh)){
      g.Clear(Color.White);
      var ft=new Font("Segoe UI",11f,FontStyle.Bold); var f=new Font("Segoe UI",9f);
      g.DrawString("Stvarna velicina  -  gore bijela podloga, dolje tamna traka",ft,Brushes.Black,20,12);
      int x=28;
      foreach(int s in sizes){ if(s>128) continue;
        g.DrawString(s+"px",f,Brushes.DimGray,x,38);
        g.FillRectangle(Brushes.White,x-6,56,s+26,s+10); g.DrawRectangle(Pens.Gainsboro,x-6,56,s+26,s+10);
        using(var b=R(s)) g.DrawImage(b,x+7,61,s,s);
        g.FillRectangle(new SolidBrush(Color.FromArgb(255,28,28,30)),x-6,56+s+16,s+26,s+10);
        using(var b=R(s)) g.DrawImage(b,x+7,61+s+16,s,s);
        x+=s+48; }
      g.DrawString("Uvecano 4x",ft,Brushes.Black,20,270);
      g.InterpolationMode=InterpolationMode.NearestNeighbor; g.PixelOffsetMode=PixelOffsetMode.Half;
      int mx=28; foreach(int s in new[]{24,32,48,64}){ using(var b=R(s)) g.DrawImage(b,mx,296,s*4,s*4);
        g.DrawString(s+"px",f,Brushes.DimGray,mx,300+s*4); mx+=s*4+22; }
      sh.Save("nbr-ladder.png",ImageFormat.Png); }
    foreach(int s in sizes) R(s).Save("nbr-"+s+".png",ImageFormat.Png);
    // the ico
    var pngs=new System.Collections.Generic.List<byte[]>();
    foreach(int s in sizes){ var ms=new MemoryStream(); R(s).Save(ms,ImageFormat.Png); pngs.Add(ms.ToArray()); }
    using(var fs=File.Create("nbr-book.ico")) using(var w=new BinaryWriter(fs)){
      w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
      int off=6+16*sizes.Length;
      for(int i=0;i<sizes.Length;i++){ w.Write((byte)(sizes[i]>=256?0:sizes[i])); w.Write((byte)(sizes[i]>=256?0:sizes[i]));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(pngs[i].Length); w.Write(off); off+=pngs[i].Length; }
      foreach(var p in pngs) w.Write(p); }
    Console.WriteLine("nbr-ladder.png + nbr-book.ico"); }
}

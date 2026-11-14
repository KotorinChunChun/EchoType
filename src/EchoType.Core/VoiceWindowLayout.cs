namespace EchoType.Core;

public readonly record struct VoiceWindowArea(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Overlaps(VoiceWindowArea other) => X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

public static class VoiceWindowLayout
{
    public static VoiceWindowArea? Find(IEnumerable<VoiceWindowArea> workAreas, VoiceWindowArea target, double width, double height, double gap = 32)
    {
        if (!double.IsFinite(gap) || gap < 0) throw new ArgumentOutOfRangeException(nameof(gap));
        var free = new List<VoiceWindowArea>();
        target = new(target.X - gap, target.Y - gap, target.Width + gap * 2, target.Height + gap * 2);
        foreach (var work in workAreas)
        {
            if (!work.Overlaps(target)) { free.Add(work); continue; }
            free.Add(new(work.X, work.Y, Math.Clamp(target.X - work.X, 0, work.Width), work.Height));
            var right = Math.Clamp(target.Right, work.X, work.Right);
            free.Add(new(right, work.Y, work.Right - right, work.Height));
            free.Add(new(work.X, work.Y, work.Width, Math.Clamp(target.Y - work.Y, 0, work.Height)));
            var bottom = Math.Clamp(target.Bottom, work.Y, work.Bottom);
            free.Add(new(work.X, bottom, work.Width, work.Bottom - bottom));
        }
        var candidates = free.Where(a => a.Width >= 240 && a.Height >= 180).Select(a =>
        {
            var w = Math.Min(width, a.Width); var h = Math.Min(height, a.Height);
            return new VoiceWindowArea(Math.Clamp(target.X, a.X, a.Right - w), Math.Clamp(target.Y, a.Y, a.Bottom - h), w, h);
        }).OrderByDescending(a => a.Width * a.Height).ThenBy(a => Math.Abs(a.X - target.X) + Math.Abs(a.Y - target.Y)).ToArray();
        return candidates.Length == 0 ? null : candidates[0];
    }
}

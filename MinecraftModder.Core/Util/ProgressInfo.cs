namespace MinecraftModder.Core.Util
{
    // Arayüze gönderilen tek tip ilerleme bilgisi. Fraction null ise "belirsiz" (dönen çubuk).
    public sealed record ProgressInfo(string Message, double? Fraction = null)
    {
        public static ProgressInfo Step(string message) => new(message);
        public static ProgressInfo Of(string message, long done, long total)
            => new(message, total > 0 ? Math.Clamp((double)done / total, 0, 1) : null);
    }
}

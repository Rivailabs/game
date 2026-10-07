namespace AstraKingdoms.Modes
{
    /// <summary>Result of submitting a command. Rejections never change state.</summary>
    public sealed class ModeReceipt
    {
        public bool Accepted { get; }
        public string Code { get; }
        public string Message { get; }

        private ModeReceipt(bool accepted, string code, string message)
        {
            Accepted = accepted;
            Code = code;
            Message = message;
        }

        public static readonly ModeReceipt Ok = new ModeReceipt(true, "OK", string.Empty);

        public static ModeReceipt Reject(string code, string message) => new ModeReceipt(false, code, message);

        public override string ToString() => Accepted ? "accepted" : "rejected " + Code + ": " + Message;
    }
}

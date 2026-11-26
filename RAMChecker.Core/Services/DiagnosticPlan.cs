namespace RAMChecker.Services;

public static partial class MemoryDiagnosticService
{
    public static string ModeName(DiagnosticMode mode) => mode switch
    {
        DiagnosticMode.Quick => "Быстрый",
        DiagnosticMode.Full => "Полный",
        DiagnosticMode.Continuous => "Циклический",
        DiagnosticMode.Stress => "Стресс",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string Describe(DiagnosticMode mode) => mode switch
    {
        DiagnosticMode.Quick => "Адресная и шахматная проверки с инверсией",
        DiagnosticMode.Stress => "Параллельное копирование и проверка инверсий",
        _ => "Движущиеся инверсии, Modulo-20 и программная чётность"
    };

    private enum Operation { Fill, Forward, Backward, ModWrite, ModDisturb, ModCheck, Parity, Copy, CheckCopy, CheckInverse }
    private record Step(Operation Kind, string Name, int PatternId, int Offset = 0, bool Inject = false, int PairMask = 1)
    {
        public bool VerifiesBlock => Kind is Operation.Backward or Operation.Parity or Operation.CheckCopy or Operation.CheckInverse;

        public long WorkBytes(long bytes)
        {
            long selected = ((bytes / 4 + 19 - Offset) / 20) * 4;
            return Kind switch
            {
                Operation.ModWrite or Operation.ModCheck => selected,
                Operation.ModDisturb => bytes - selected,
                Operation.Forward or Operation.Backward or Operation.CheckCopy => bytes * 2,
                _ => bytes
            };
        }
    }

    private static List<Step> BuildPlan(DiagnosticMode mode, int cycle)
    {
        var steps = new List<Step>();
        int random = unchecked(cycle * 4) & 0x7ffffffc;
        if (random == 0) random = 4;
        if (mode == DiagnosticMode.Stress)
        {
            // Each sweep pairs different regions. Reverse the source and destination on the next cycle.
            foreach (int mask in new[] { 1, 8, 32, 128 })
            {
                int pattern = random ^ (mask << 4);
                if (pattern == 0) pattern = 4;
                int destination = (cycle & 1) == 1 ? mask : 0;
                steps.Add(new(Operation.Fill, "Заполнение", pattern));
                steps.Add(new(Operation.Copy, "Копирование", pattern, destination, PairMask: mask));
                steps.Add(new(Operation.CheckCopy, "Проверка копии и инверсия", pattern, destination, PairMask: mask));
                steps.Add(new(Operation.CheckInverse, "Проверка инверсии", pattern, destination, PairMask: mask));
            }
            return steps;
        }
        void Inversions(int pattern)
        {
            string name = PatternName(pattern);
            steps.Add(new(Operation.Fill, name + " · " + Steps[0], pattern, Inject: true));
            steps.Add(new(Operation.Forward, name + " · " + Steps[1], pattern));
            steps.Add(new(Operation.Backward, name + " · " + Steps[2], pattern));
        }
        Inversions(1);
        Inversions(2);
        if (mode == DiagnosticMode.Quick) return steps;
        for (int shift = 0; shift < 32; shift++) Inversions(3 + shift * 4);
        Inversions(random);
        for (int offset = 0; offset < 20; offset++)
        {
            string name = $"Modulo-20 · {offset + 1}/20";
            steps.Add(new(Operation.ModWrite, name, random, offset));
            steps.Add(new(Operation.ModDisturb, name, random, offset));
            steps.Add(new(Operation.ModDisturb, name, random, offset));
            steps.Add(new(Operation.ModCheck, name, random, offset));
        }
        steps.Add(new(Operation.Fill, "Чётность · Заполнение", random, Inject: true));
        steps.Add(new(Operation.Parity, "Чётность", random));
        return steps;
    }

    private static long RunStep(Step step, Span<uint> data, long first, Span<uint> partner, int block,
        Action<long, uint, uint> onError, CancellationToken token)
    {
        long bytes = (long)data.Length * 4;
        switch (step.Kind)
        {
            case Operation.Fill:
                FillBlock(data, first, step.PatternId, token);
                return bytes;
            case Operation.Forward:
            case Operation.Backward:
                CheckBlock(data, first, step.PatternId, step.Kind == Operation.Backward, onError, token);
                return bytes * 2;
            case Operation.Copy:
                if ((block & step.PairMask) != step.Offset) return 0;
                for (int start = 0; start < data.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int size = Math.Min(65536, data.Length - start);
                    partner.Slice(start, size).CopyTo(data.Slice(start, size));
                    start += size;
                }
                return bytes * 2;
            case Operation.CheckCopy:
            case Operation.CheckInverse:
            case Operation.Parity:
                long source = step.Kind != Operation.Parity && (block & step.PairMask) == step.Offset
                    ? (long)(block ^ step.PairMask) * data.Length : first;
                for (int i = 0; i < data.Length; i++)
                {
                    if ((i & CancellationCheckMask) == 0) token.ThrowIfCancellationRequested();
                    uint expected = Pattern(source + i, step.PatternId);
                    if (step.Kind == Operation.CheckInverse) expected = ~expected;
                    uint actual = data[i];
                    if (actual != expected) onError(first + i, expected, actual);
                    if (step.Kind == Operation.CheckCopy) data[i] = ~expected;
                }
                return step.Kind == Operation.CheckCopy ? bytes * 2 : bytes;
            default:
                // Each residue is relative to the whole allocation, including blocks shorter than 20 words.
                int residue = (int)((step.Offset + 20 - first % 20) % 20);
                long selected = ((long)data.Length + 19 - residue) / 20;
                uint value = Pattern(step.Offset, step.PatternId);
                if (step.Kind == Operation.ModDisturb)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        if ((i & CancellationCheckMask) == 0) token.ThrowIfCancellationRequested();
                        if (i % 20 != residue) data[i] = ~value;
                    }
                    return bytes - selected * 4;
                }
                for (long i = residue; i < data.Length; i += 20)
                {
                    if (((i - residue) & CancellationCheckMask) == 0) token.ThrowIfCancellationRequested();
                    if (step.Kind == Operation.ModWrite) data[(int)i] = value;
                    else if (data[(int)i] != value) onError(first + i, value, data[(int)i]);
                }
                return selected * 4;
        }
    }
}

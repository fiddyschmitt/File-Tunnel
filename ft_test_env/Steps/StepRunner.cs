using System.Diagnostics;

namespace ft_test_env.Steps
{
    public enum StepStatus { Ok, Skipped, Failed }

    public readonly record struct StepOutcome(StepStatus Status, string? Detail)
    {
        public static StepOutcome Ok(string? detail = null) => new(StepStatus.Ok, detail);
        public static StepOutcome Skip(string? detail = null) => new(StepStatus.Skipped, detail);
        public static StepOutcome Fail(string? detail) => new(StepStatus.Failed, detail);
    }

    /// <summary>A step that can report a live progress string (e.g. download percentage).</summary>
    public delegate StepOutcome StepAction(Action<string> report);

    /// <summary>
    /// Runs named steps and prints "- description ... [ OK ]/[SKIP]/[FAIL] detail (123 ms)".
    /// While a step runs on an interactive console, an animated spinner (and any progress the
    /// step reports) is shown on the same line. Tracks whether every step succeeded.
    ///
    /// Not safe for concurrent Run calls — steps are expected to run one at a time.
    /// </summary>
    public class StepRunner
    {
        private static readonly char[] SpinnerFrames = ['|', '/', '-', '\\'];

        // Column at which the status label (and the live spinner) begins, so [ OK ]/[FAIL]/[SKIP]
        // line up vertically regardless of description length.
        private const int LabelColumn = 64;

        public bool AllSucceeded { get; private set; } = true;

        public void Section(string title)
        {
            Console.WriteLine();
            var original = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));
            Console.ForegroundColor = original;
        }

        /// <summary>Run a step that may report progress and reports its own outcome. Exceptions become Fail.</summary>
        public bool Run(string description, StepAction step)
        {
            var prefix = BuildPrefix(description);
            var sw = Stopwatch.StartNew();

            var statusLock = new object();
            var status = "";
            void Report(string s) { lock (statusLock) status = s; }

            StepOutcome outcome;

            if (Console.IsOutputRedirected)
            {
                // No spinner when piped/redirected: write the prefix, run, then the outcome.
                Console.Write(prefix);
                outcome = Invoke(step, Report);
                sw.Stop();
                WriteOutcome(outcome, sw.ElapsedMilliseconds);
            }
            else
            {
                var stop = false;
                var spinner = new Thread(() =>
                {
                    var frame = 0;
                    while (!Volatile.Read(ref stop))
                    {
                        string current;
                        lock (statusLock) current = status;
                        RenderLive(prefix, SpinnerFrames[frame % SpinnerFrames.Length], current);
                        frame++;
                        Thread.Sleep(120);
                    }
                })
                { IsBackground = true };
                spinner.Start();

                outcome = Invoke(step, Report);

                Volatile.Write(ref stop, true);
                spinner.Join();
                sw.Stop();

                ClearLine();
                Console.Write(prefix);
                WriteOutcome(outcome, sw.ElapsedMilliseconds);
            }

            if (outcome.Status == StepStatus.Failed)
            {
                AllSucceeded = false;
            }

            return outcome.Status != StepStatus.Failed;
        }

        /// <summary>Run a step that reports its own outcome. Exceptions become Fail.</summary>
        public bool Run(string description, Func<StepOutcome> step) => Run(description, _ => step());

        /// <summary>Run a step that throws on failure and succeeds otherwise.</summary>
        public bool Run(string description, Action step) =>
            Run(description, _ => { step(); return StepOutcome.Ok(); });

        /// <summary>Run a step that returns true on success.</summary>
        public bool Run(string description, Func<bool> step) =>
            Run(description, _ => step() ? StepOutcome.Ok() : StepOutcome.Fail(null));

        private static StepOutcome Invoke(StepAction step, Action<string> report)
        {
            try
            {
                return step(report);
            }
            catch (Exception ex)
            {
                return StepOutcome.Fail(ex.Message);
            }
        }

        // Builds "  - {description} " then dot-fills to LabelColumn so the status label always
        // starts in the same column. Descriptions longer than the column fall back to a single space.
        private static string BuildPrefix(string description)
        {
            var head = $"  - {description} ";
            if (head.Length >= LabelColumn)
            {
                return head;
            }
            return head + new string('.', LabelColumn - head.Length - 1) + " ";
        }

        private static int Width()
        {
            try
            {
                var w = Console.WindowWidth;
                return w > 20 ? w : 80;
            }
            catch
            {
                return 80;
            }
        }

        private static void RenderLive(string prefix, char spinnerFrame, string status)
        {
            var text = string.IsNullOrEmpty(status)
                ? $"{prefix}{spinnerFrame}"
                : $"{prefix}{spinnerFrame} {status}";

            var max = Width() - 1;
            text = text.Length > max ? text[..max] : text.PadRight(max);

            Console.Write('\r');
            Console.Write(text);
        }

        private static void ClearLine()
        {
            Console.Write('\r');
            Console.Write(new string(' ', Width() - 1));
            Console.Write('\r');
        }

        private static void WriteOutcome(StepOutcome outcome, long elapsedMs)
        {
            var original = Console.ForegroundColor;

            var (label, color) = outcome.Status switch
            {
                StepStatus.Ok => ("[ OK ]", ConsoleColor.Green),
                StepStatus.Skipped => ("[SKIP]", ConsoleColor.Yellow),
                _ => ("[FAIL]", ConsoleColor.Red)
            };

            Console.ForegroundColor = color;
            Console.Write(label);
            Console.ForegroundColor = original;

            if (!string.IsNullOrWhiteSpace(outcome.Detail))
            {
                Console.Write($" {outcome.Detail}");
            }

            Console.WriteLine($"  ({elapsedMs:N0} ms)");
        }
    }
}

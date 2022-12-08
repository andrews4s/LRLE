using System;
using System.Diagnostics;

namespace LREParser
{
    partial class MainClass
    {
        class ConsoleStopWatch : IDisposable
        {
            private readonly string subject;
            private readonly Stopwatch stopwatch;

            public ConsoleStopWatch(string subject)
            {
                this.subject = subject;
                this.stopwatch = new Stopwatch();
                this.stopwatch.Start();
            }

            public void Dispose()
            {
                this.Stop();
            }

            private void Stop()
            {
                if (this.stopwatch.IsRunning)
                {
                    this.stopwatch.Stop();
                }
                Console.WriteLine($"{subject} finished in {(stopwatch.ElapsedMilliseconds / 1000.0)} s");
            }
        }
    }
}



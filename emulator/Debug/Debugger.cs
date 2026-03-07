namespace nesbox.Debug;
using SER = StringExpressionEvaluator.StringExpressionEvaluator;

public static class Debugger {
    internal struct SourceAddress {
        internal string fp;
        internal int    line;
    }
    
    internal static void BeginDebugging() {
        
    }

    internal static void StepOnce() {
        Step();
        Renderer.Present();     // present instantly
    }

    internal static void StepOver() {
        if (System.Register.IR is 0x20 /* jsr */ ) {
            _lastSp = System.Register.S;
            while (System.Register.S != _lastSp) {
                if (Step()) return;
            }
        } else if (_lastLineNumber > _currentLineNumber) {
            while (_lastLineNumber < _currentLineNumber) {
                if (Step()) return;
            }
        } else {
            Step();   
        }
        Renderer.Present();     // present instantly
    }
    
    private static bool Step() {
        if (System.cycle is 0) {
            _lastLineNumber    = _currentLineNumber;
            _currentLineNumber = SourceCodeReferences[System.PC].line;
        }

        System.Step();
        ++System.virtualTime;   // bump virtual time (emu thread sleeping while debugging)

        if (BreakPoints.Find(t => t!.Value.pos == _currentLineNumber) is { } breakpoint) {
            if (breakpoint.expr is null) return true; // unconditional breakpoint or conditional breakpoint leak
            if (!SER.TryEvaluate(ref breakpoint.expr, out var result, Symbols)) return true;
            if (result is 0) return false;

            return true;
            // Update Debugger in IDE (DAP)
        }
        
        NoBreakPoint:
        
        // Update Debugger in IDE (DAP)
        return false;    // no breakpoint
    }

    private static          Dictionary<string, SER.SerUnion<int>> Symbols;
    private static          List<(int pos, string? expr)?>        BreakPoints;
    private static readonly Dictionary<int, SourceAddress>        SourceCodeReferences = [];
    private static          int                                   _lastLineNumber;
    private static          int                                   _currentLineNumber;
    private static          byte                                  _lastSp;
    
    internal static bool debugging;
}
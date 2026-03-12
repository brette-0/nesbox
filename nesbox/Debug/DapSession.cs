using System.Numerics;

namespace nesbox.Debug;

public class DapSession<T> : API.Debugging.IDebugAdaptorProtocol<T> where T : IBinaryInteger<T> {
    public int GetSymbol(string sym) {
        throw new NotImplementedException();
    }
    public IDictionary<string, int> GetSymbols(IReadOnlyList<string> syms) {
        throw new NotImplementedException();
    }

    public API.Debugging.IDebugFile<T> debugFile { get; }
}
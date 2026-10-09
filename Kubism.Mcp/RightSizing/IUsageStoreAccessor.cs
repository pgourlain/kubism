using Kubism.Core.RightSizing;

namespace Kubism.Mcp.RightSizing;

public interface IUsageStoreAccessor
{
    IUsageStore Store { get; }
    ITokenProvider? Tokens { get; }
}

using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpVideoTimingContractResolver
{
    WanGpVideoTimingContract Resolve(WanGpModelSchema schema, int requestedDurationSeconds);
}

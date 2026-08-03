using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpAudioInputContractResolver
{
    WanGpAudioInputContract Resolve(WanGpModelInfo model, WanGpModelSchema schema);
}

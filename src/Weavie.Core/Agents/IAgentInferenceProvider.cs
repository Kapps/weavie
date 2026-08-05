using Weavie.Core.Inference;

namespace Weavie.Core.Agents;

/// <summary>An agent provider whose installed CLI can also run isolated, typed ad-hoc inference.</summary>
public interface IAgentInferenceProvider : IAgentProvider, IInferenceProvider;

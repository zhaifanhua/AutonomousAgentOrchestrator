namespace AgentOrchestrator.Infrastructure.LLMClients;

public class LLMClientException(string message, Exception? inner = null)
    : Exception(message, inner);
export interface WeavieLspServer {
  id: string;
  languageIds: string[];
  settings: Record<string, unknown> | null;
}

export interface WeavieLspConfig {
  workspace: string;
  servers: WeavieLspServer[];
}

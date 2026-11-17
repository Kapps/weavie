export interface WeavieLspServer {
  id: string;
  languageIds: string[];
  settings: Record<string, unknown> | null;
}

export interface WeavieLspConfig {
  workspace: string;
  /** Bumped by Restart Language Servers; any config change restarts the session's clients. */
  revision: number;
  servers: WeavieLspServer[];
}

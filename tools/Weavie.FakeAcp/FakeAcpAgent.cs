using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.FakeAcp;

namespace Weavie.FakeAcp;

internal sealed class FakeAcpAgent : IAcpAgent {
	private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Lock _gate = new();
	private readonly string? _fakeMode;
	private readonly bool _requiresAuthentication;
	private readonly bool _holdsClose =
		Environment.GetEnvironmentVariable("WEAVIE_FAKE_ACP_MODE") == "held-close";
	private AcpAgentConnection? _connection;
	private TaskCompletionSource<string>? _heldPrompt;
	private string _mode = "default";
	private string _model = "alpha";
	private bool _fast;
	private bool _cancelFails;
	private bool _authenticated;
	private bool _expiredAuthentication;
	private bool _supportsPlanUpdates;
	private string? _sessionId;

	public FakeAcpAgent() {
		_fakeMode = Environment.GetEnvironmentVariable("WEAVIE_FAKE_ACP_MODE");
		_requiresAuthentication = _fakeMode is
			"held-authentication" or "agent-authentication" or "terminal-authentication";
	}

	public Task TerminalFailure => _never.Task;

	public void Attach(AcpAgentConnection connection) =>
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));

	public async Task<JsonNode> HandleRequestAsync(
		JsonElement requestId,
		string method,
		JsonElement parameters,
		CancellationToken ct) =>
		method switch {
			"initialize" => Initialize(parameters),
			"authenticate" => await AuthenticateAsync(ct).ConfigureAwait(false),
			"session/new" => Open(parameters, "fake-session", replay: false),
			"session/load" => Open(
				parameters,
				AcpJson.RequiredString(parameters, "sessionId", method),
				replay: true),
			"session/resume" => Open(
				parameters,
				AcpJson.RequiredString(parameters, "sessionId", method),
				replay: false),
			"session/close" => await CloseAsync(parameters, ct).ConfigureAwait(false),
			"session/prompt" => await PromptAsync(parameters, ct).ConfigureAwait(false),
			"session/set_mode" => SetMode(parameters),
			"session/set_config_option" => await SetConfigAsync(parameters, ct).ConfigureAwait(false),
			"_session/steering" => Steer(parameters),
			_ => throw new AcpAdapterException(-32601, $"Unknown fake ACP method '{method}'.", null),
		};

	public Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken ct) {
		if (method == "session/cancel") {
			if (_cancelFails) throw new InvalidOperationException("Synthetic downstream interrupt failure.");
			TaskCompletionSource<string>? held;
			lock (_gate) held = _heldPrompt;
			held?.TrySetResult("cancelled");
		}
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync() {
		TaskCompletionSource<string>? held;
		lock (_gate) held = _heldPrompt;
		held?.TrySetCanceled();
		return ValueTask.CompletedTask;
	}

	private JsonObject Initialize(JsonElement parameters) {
		if (!parameters.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != 1) {
			throw new AcpAdapterException(-32600, "Fake ACP requires protocol version 1.", null);
		}
		_supportsPlanUpdates = parameters.TryGetProperty("clientCapabilities", out var capabilities)
			&& capabilities.TryGetProperty("plan", out var plan)
			&& plan.ValueKind == JsonValueKind.Object;
		if (!_supportsPlanUpdates) {
			throw new AcpAdapterException(-32600, "Fake ACP requires plan document support.", null);
		}
		var response = new JsonObject {
			["protocolVersion"] = 1,
			["agentInfo"] = new JsonObject { ["name"] = "weavie-fake-acp", ["version"] = "1" },
			["authMethods"] = _requiresAuthentication
				? new JsonArray(_fakeMode == "terminal-authentication"
					? new JsonObject {
						["id"] = "fake-terminal-login",
						["name"] = "Fake terminal login",
						["type"] = "terminal",
						["args"] = new JsonArray("terminal-login"),
						["env"] = new JsonObject { ["FAKE_LOGIN"] = "1" },
					}
					: new JsonObject {
						["id"] = "fake-login",
						["name"] = "Fake login",
					})
				: [],
			["_meta"] = new JsonObject { ["steering"] = new JsonObject { ["supported"] = true } },
		};
		if (_fakeMode != "minimal-capabilities") response["agentCapabilities"] = new JsonObject {
			["loadSession"] = _fakeMode != "resume-only",
			["promptCapabilities"] = new JsonObject { ["image"] = true, ["embeddedContext"] = true },
			["sessionCapabilities"] = new JsonObject {
				["resume"] = new JsonObject(),
				["close"] = new JsonObject(),
			},
			["mcpCapabilities"] = new JsonObject { ["http"] = true, ["sse"] = false },
		};
		return response;
	}

	private JsonObject Open(JsonElement parameters, string sessionId, bool replay) {
		if (_fakeMode == "terminal-authentication"
			&& File.Exists(Path.Combine(Environment.CurrentDirectory, "terminal-authenticated"))) {
			_authenticated = true;
		}
		if (_requiresAuthentication && !_authenticated) {
			throw new AcpAdapterException(-32000, "Sign in to the fake ACP agent.", null);
		}
		if (_fakeMode == "minimal-capabilities") RequireStdioMcp(parameters);
		else RequireMcp(parameters);
		_sessionId = sessionId;
		if (replay && sessionId == "replay-session") {
			Update(new JsonObject {
				["sessionUpdate"] = "user_message_chunk",
				["messageId"] = "replayed-user-1",
				["content"] = Text("first persisted "),
			});
			Update(new JsonObject {
				["sessionUpdate"] = "user_message_chunk",
				["messageId"] = "replayed-user-1",
				["content"] = Text("prompt"),
			});
			ReplayProgress("first persisted progress");
			PlanDocument("replayed-plan-1", "# First persisted plan");
			Update(new JsonObject {
				["sessionUpdate"] = "agent_message_chunk",
				["messageId"] = "replayed-agent-1",
				["content"] = Text("first persisted response"),
			});
			Update(new JsonObject {
				["sessionUpdate"] = "user_message_chunk",
				["messageId"] = "replayed-guidance",
				["content"] = Resource("weavie://instructions", "hidden guidance"),
			});
			Update(new JsonObject {
				["sessionUpdate"] = "user_message_chunk",
				["messageId"] = "replayed-selection",
				["content"] = Resource("file:///workspace/file.cs#selection", "hidden selection"),
			});
			Update(new JsonObject {
				["sessionUpdate"] = "user_message_chunk",
				["messageId"] = "replayed-user-2",
				["content"] = Text("second persisted prompt"),
			});
			ReplayProgress("second persisted progress");
			PlanDocument("replayed-plan-2", "# Second persisted plan");
			Update(new JsonObject {
				["sessionUpdate"] = "agent_message_chunk",
				["messageId"] = "replayed-agent-2",
				["content"] = Text("persisted transcript"),
			});
			// A finished tool replays as two frames, the first always non-terminal — so a load must judge what
			// is still running only once the replay is over, not frame by frame.
			Update(new JsonObject {
				["sessionUpdate"] = "tool_call",
				["toolCallId"] = "replayed-finished",
				["title"] = "Persisted finished task",
				["kind"] = "execute",
				["status"] = "pending",
			});
			Update(new JsonObject {
				["sessionUpdate"] = "tool_call_update",
				["toolCallId"] = "replayed-finished",
				["status"] = "completed",
			});
			Update(new JsonObject {
				["sessionUpdate"] = "tool_call",
				["toolCallId"] = "replayed-background",
				["title"] = "Persisted background task",
				["kind"] = "execute",
				["status"] = "in_progress",
			});
		}
		Update(new JsonObject {
			["sessionUpdate"] = "available_commands_update",
			["availableCommands"] = new JsonArray(new JsonObject {
				["name"] = "compact",
				["description"] = "Compact the fake transcript.",
			}),
		});
		var response = Setup();
		response["sessionId"] = sessionId;
		return response;
	}

	private async Task<JsonNode> AuthenticateAsync(CancellationToken ct) {
		if (!_requiresAuthentication) return new JsonObject();
		if (_fakeMode == "agent-authentication") {
			_authenticated = true;
			return new JsonObject();
		}
		File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "authentication-started"), string.Empty);
		try {
			await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			_authenticated = true;
			File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "authentication-completed"), string.Empty);
		}
		return new JsonObject();
	}

	private async Task<JsonObject> CloseAsync(JsonElement parameters, CancellationToken ct) {
		RequireSession(parameters);
		if (_holdsClose) {
			File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "close-started"), string.Empty);
			await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
		}
		_sessionId = null;
		return [];
	}

	private async Task<JsonNode> PromptAsync(JsonElement parameters, CancellationToken ct) {
		RequireSession(parameters);
		var prompt = AcpJson.RequiredArray(parameters, "prompt", "session/prompt");
		string text = PromptText(prompt);
		if (text is "hold" or "hold-cancel-error") {
			_cancelFails = text == "hold-cancel-error";
			return await HoldAsync(ct).ConfigureAwait(false);
		}
		if (text == "restart-update-race") return await RestartUpdateRaceAsync(ct).ConfigureAwait(false);
		if (text == "rich") RichUpdates();
		else if (text == "background") StartBackground();
		else if (text == "finish-background") FinishBackground();
		else if (text == "prompt-failure") PromptFailure();
		else if (text == "shared-message-id") SharedMessageId();
		else if (text == "tool-content") ToolContent();
		else if (text == "empty-diff") EmptyDiff();
		else if (text == "refusal") {
			RichUpdates();
			return new JsonObject { ["stopReason"] = "refusal" };
		} else if (text == "malformed-update") {
			Connection().Notify("session/update", new JsonObject { ["sessionId"] = _sessionId });
		} else if (text == "plan-document") PlanDocument("live-plan", "# Implementation plan");
		else if (text == "plan-revision") PlanDocument("live-plan", "# Revised implementation plan");
		else if (text == "remove-plan") RemovePlan("live-plan");
		else if (text == "item-plan-document") ItemPlanDocument();
		else if (text == "file-plan-document") FilePlanDocument();
		else if (text == "external-file-plan-document") ExternalFilePlanDocument();
		else if (text == "auth-expired" && !_expiredAuthentication) {
			_expiredAuthentication = true;
			_authenticated = false;
			throw new AcpAdapterException(-32000, "Fake credentials expired.", null);
		} else if (text == "permission") await PermissionAsync(ct).ConfigureAwait(false);
		else if (text == "input") await InputAsync(ct).ConfigureAwait(false);
		else if (text == "input-cancel") await InputActionAsync(ct).ConfigureAwait(false);
		else if (text == "input-null-options") await NullOptionalsInputAsync(ct).ConfigureAwait(false);
		else if (text == "input-default-schema") await DefaultSchemaInputAsync(ct).ConfigureAwait(false);
		else if (text == "input-titled-array") await TitledArrayInputAsync(ct).ConfigureAwait(false);
		else if (text == "url-input") await UrlInputAsync("https://example.test/login", ct).ConfigureAwait(false);
		else if (text == "unsafe-url") await UrlInputAsync("javascript:alert(1)", ct).ConfigureAwait(false);
		else if (text == "password-input") await PasswordInputAsync(ct).ConfigureAwait(false);
		else if (text is "terminal-output" or "terminal-null-optionals") {
			await TerminalOutputAsync(text == "terminal-null-optionals", ct).ConfigureAwait(false);
		} else if (text == "terminal-cancel") await TerminalCancellationAsync(ct).ConfigureAwait(false);
		else if (text == "agent-terminal") AgentOwnedTerminal();
		else if (text == "cancel-before-dispatch") await CancelBeforeDispatchAsync().ConfigureAwait(false);
		else if (text == "terminal-failure") await TerminalFailureAsync(ct).ConfigureAwait(false);
		else if (text.StartsWith("fs-empty:", StringComparison.Ordinal)) {
			await FileSystemAsync(text[9..], string.Empty, ct).ConfigureAwait(false);
		} else if (text.StartsWith("fs:", StringComparison.Ordinal)) {
			await FileSystemAsync(text[3..], "written through ACP", ct).ConfigureAwait(false);
		} else if (text.StartsWith("persist-probe:", StringComparison.Ordinal)) {
			await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
			File.WriteAllText(text["persist-probe:".Length..], "provider mutation");
			Message("persistence failure did not stop the provider");
		} else if (text is "context" or "context-after-reset") ContextResult(prompt, text);
		else if (text == "control-state") Message($"control state: {_model}/{_mode}/{_fast}");
		else if (text == "echo-user") {
			Update(new JsonObject {
				["sessionUpdate"] = "user_message_chunk",
				["messageId"] = "live-user-echo",
				["content"] = Text(text),
			});
			Message("echo: " + text);
		} else if (text == "image") Message("image=" + prompt.EnumerateArray().Any(
			  block => AcpJson.OptionalString(block, "type") == "image"));
		else if (text == "crash") Environment.Exit(19);
		else Message("echo: " + text);
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private async Task<JsonNode> HoldAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) _heldPrompt = completion;
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "hold",
			["title"] = "Waiting for steering",
			["kind"] = "execute",
			["status"] = "in_progress",
		});
		using var registration = ct.Register(() => completion.TrySetCanceled(ct));
		string result = await completion.Task.ConfigureAwait(false);
		lock (_gate) _heldPrompt = null;
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "hold",
			["status"] = result == "cancelled" ? "failed" : "completed",
		});
		Message("steered: " + result);
		return new JsonObject { ["stopReason"] = result == "cancelled" ? "cancelled" : "end_turn" };
	}

	private async Task<JsonNode> RestartUpdateRaceAsync(CancellationToken ct) {
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "restart-update-race",
			["title"] = "Waiting to publish a concurrent update",
			["kind"] = "execute",
			["status"] = "in_progress",
		});
		string trigger = Path.Combine(Environment.CurrentDirectory, "release-restart-update");
		_ = Task.Run(async () => {
			while (!File.Exists(trigger)) await Task.Delay(10, ct).ConfigureAwait(false);
			Message("stale update from replaced generation");
			File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "restart-update-sent"), string.Empty);
		}, ct);
		await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
		return new JsonObject();
	}

	private JsonObject Steer(JsonElement parameters) {
		RequireSession(parameters);
		string text = PromptText(AcpJson.RequiredArray(parameters, "prompt", "_session/steering"));
		if (text == "complete-url") {
			Connection().Notify("elicitation/complete", new JsonObject {
				["elicitationId"] = "fake-browser-login",
			});
			Message("URL completion notification sent");
			return new JsonObject { ["outcome"] = "injected" };
		}
		TaskCompletionSource<string>? held;
		lock (_gate) held = _heldPrompt;
		if (held is null || held.Task.IsCompleted) return new JsonObject { ["outcome"] = "promptRequired" };
		held.TrySetResult(text);
		return new JsonObject { ["outcome"] = "injected" };
	}

	private void RichUpdates() {
		Update(new JsonObject {
			["sessionUpdate"] = "agent_thought_chunk",
			["messageId"] = "thought",
			["content"] = Text("inspect"),
		});
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "edit",
			["title"] = "Edit file",
			["kind"] = "edit",
			["status"] = "in_progress",
			["locations"] = new JsonArray(new JsonObject {
				["path"] = Path.Combine(Environment.CurrentDirectory, "sample.txt"),
				["line"] = 7,
			}),
		});
		Update(new JsonObject {
			["sessionUpdate"] = "plan",
			["entries"] = new JsonArray(
				new JsonObject { ["content"] = "Inspect", ["status"] = "completed", ["priority"] = "medium" },
				new JsonObject { ["content"] = "Implement", ["status"] = "in_progress", ["priority"] = "high" }),
		});
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "edit",
			["status"] = "completed",
			["content"] = new JsonArray(new JsonObject {
				["type"] = "diff",
				["path"] = Path.Combine(Environment.CurrentDirectory, "sample.txt"),
				["oldText"] = "old",
				["newText"] = "new",
			}),
		});
		Update(new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 123, ["size"] = 4096 });
		// Usage windows ride the same update through Claude's vendor _meta extension.
		Update(new JsonObject {
			["sessionUpdate"] = "usage_update",
			["used"] = 123,
			["size"] = 4096,
			["_meta"] = new JsonObject {
				["_claude/rateLimit"] = new JsonObject {
					["status"] = "allowed_warning",
					["rateLimitType"] = "seven_day",
					["utilization"] = 0.62,
					["resetsAt"] = 4102444800,
				},
			},
		});
		Message("rich response");
	}

	private void SharedMessageId() {
		foreach (string text in new[] { "deep ", "thought" }) {
			Update(new JsonObject {
				["sessionUpdate"] = "agent_thought_chunk",
				["messageId"] = "shared-api-message",
				["content"] = Text(text),
			});
		}
		foreach (string text in new[] { "final ", "answer" }) {
			Update(new JsonObject {
				["sessionUpdate"] = "agent_message_chunk",
				["messageId"] = "shared-api-message",
				["content"] = Text(text),
			});
		}
	}

	private void ToolContent() {
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "content",
			["title"] = "Rich tool content",
			["kind"] = "read",
			["status"] = "in_progress",
		});
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "content",
			["status"] = "completed",
			["content"] = new JsonArray(
				Content(Text("tool text")),
				Content(new JsonObject {
					["type"] = "image",
					["mimeType"] = "image/png",
					["data"] = "aW1hZ2U=",
				}),
				Content(new JsonObject {
					["type"] = "resource_link",
					["uri"] = "https://example.test/result",
					["name"] = "Result",
				}),
				Content(new JsonObject {
					["type"] = "resource",
					["resource"] = new JsonObject {
						["uri"] = "file:///result.txt",
						["mimeType"] = "text/plain",
						["text"] = "embedded text",
					},
				})),
		});
	}

	private void EmptyDiff() {
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "empty-diff",
			["title"] = "Empty file",
			["kind"] = "edit",
			["status"] = "in_progress",
		});
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "empty-diff",
			["status"] = "completed",
			["content"] = new JsonArray(new JsonObject {
				["type"] = "diff",
				["path"] = Path.Combine(Environment.CurrentDirectory, "empty.txt"),
				["oldText"] = "before",
				["newText"] = string.Empty,
			}),
		});
	}

	private static JsonObject Content(JsonObject block) => new() {
		["type"] = "content",
		["content"] = block,
	};

	private void StartBackground() => Update(new JsonObject {
		["sessionUpdate"] = "tool_call",
		["toolCallId"] = "subagent",
		["title"] = "Background agent",
		["kind"] = "execute",
		["status"] = "in_progress",
	});

	private void FinishBackground() {
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "subagent",
			["status"] = "completed",
		});
		Message("background finished");
	}

	private void PromptFailure() {
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "foreground-failure",
			["title"] = "Foreground command",
			["kind"] = "execute",
			["status"] = "in_progress",
		});
		StartBackground();
		throw new AcpAdapterException(-32009, "Synthetic prompt failure.", null);
	}

	private async Task PermissionAsync(CancellationToken ct) {
		var result = await Connection().RequestAsync("session/request_permission", new JsonObject {
			["sessionId"] = _sessionId,
			["toolCall"] = new JsonObject {
				["toolCallId"] = "permission-tool",
				["title"] = "Run protected action",
				["kind"] = "execute",
				["rawInput"] = "protected",
			},
			["options"] = new JsonArray(
				new JsonObject { ["optionId"] = "allow-once", ["name"] = "Allow", ["kind"] = "allow_once" },
				new JsonObject { ["optionId"] = "allow-always", ["name"] = "Always allow", ["kind"] = "allow_always" },
				new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" }),
		}, ct).ConfigureAwait(false);
		Message("permission: " + result.GetProperty("outcome").GetProperty("optionId").GetString());
	}

	private async Task InputAsync(CancellationToken ct) {
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "form",
			["message"] = "Choose a value",
			["requestedSchema"] = new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["choice"] = new JsonObject {
						["type"] = "string",
						["title"] = "Choice",
						["description"] = "Which value?",
						["oneOf"] = new JsonArray(
							new JsonObject { ["const"] = "one", ["title"] = "One" },
							new JsonObject { ["const"] = "two", ["title"] = "Two" }),
					},
				},
				["required"] = new JsonArray("choice"),
			},
		}, ct).ConfigureAwait(false);
		Message("input: " + result.GetProperty("content").GetProperty("choice").GetString());
	}

	private async Task InputActionAsync(CancellationToken ct) {
		var result = await Connection().RequestAsync(
			"elicitation/create",
			FormElicitation(new JsonObject {
				["value"] = new JsonObject { ["type"] = "string", ["title"] = "Value" },
			}),
			ct).ConfigureAwait(false);
		Message("input action: " + AcpJson.RequiredString(result, "action", "elicitation response"));
	}

	private async Task NullOptionalsInputAsync(CancellationToken ct) {
		var result = await Connection().RequestAsync(
			"elicitation/create",
			FormElicitation(new JsonObject {
				["text"] = new JsonObject {
					["type"] = "string",
					["title"] = "Text",
					["enum"] = null,
					["oneOf"] = null,
				},
				["values"] = new JsonObject {
					["type"] = "array",
					["title"] = "Values",
					["items"] = new JsonObject {
						["type"] = "string",
						["enum"] = null,
						["anyOf"] = null,
					},
				},
			}),
			ct).ConfigureAwait(false);
		var content = result.GetProperty("content");
		Message($"null options: {content.GetProperty("text").GetString()} | "
			+ string.Join(",", content.GetProperty("values").EnumerateArray().Select(value => value.GetString())));
	}

	private async Task DefaultSchemaInputAsync(CancellationToken ct) {
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "form",
			["message"] = "Confirm the empty form",
			["requestedSchema"] = new JsonObject(),
		}, ct).ConfigureAwait(false);
		Message("default schema action: " + AcpJson.RequiredString(result, "action", "elicitation response"));
	}

	private async Task TitledArrayInputAsync(CancellationToken ct) {
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "form",
			["message"] = "Choose values",
			["requestedSchema"] = new JsonObject {
				["properties"] = new JsonObject {
					["values"] = new JsonObject {
						["type"] = "array",
						["title"] = "Values",
						["items"] = new JsonObject {
							["anyOf"] = new JsonArray(
								new JsonObject { ["const"] = "one", ["title"] = "One" },
								new JsonObject { ["const"] = "two", ["title"] = "Two" }),
						},
					},
				},
				["required"] = new JsonArray("values"),
			},
		}, ct).ConfigureAwait(false);
		Message("titled array: " + string.Join(",", result.GetProperty("content")
			.GetProperty("values").EnumerateArray().Select(value => value.GetString())));
	}

	private async Task UrlInputAsync(string url, CancellationToken ct) {
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "url",
			["elicitationId"] = "fake-browser-login",
			["message"] = "Authenticate in your browser",
			["url"] = url,
		}, ct).ConfigureAwait(false);
		Message("URL action: " + AcpJson.RequiredString(result, "action", "URL elicitation response"));
	}

	private async Task PasswordInputAsync(CancellationToken ct) {
		await Connection().RequestAsync(
			"elicitation/create",
			FormElicitation(new JsonObject {
				["credential"] = new JsonObject {
					["type"] = "string",
					["title"] = "Credential",
					["format"] = "password",
				},
			}),
			ct).ConfigureAwait(false);
	}

	private JsonObject FormElicitation(JsonObject properties) => new() {
		["sessionId"] = _sessionId,
		["mode"] = "form",
		["message"] = "Provide a value",
		["requestedSchema"] = new JsonObject {
			["type"] = "object",
			["properties"] = properties,
		},
	};

	private async Task FileSystemAsync(string path, string content, CancellationToken ct) {
		await Connection().RequestAsync("fs/write_text_file", new JsonObject {
			["sessionId"] = _sessionId,
			["path"] = path,
			["content"] = content,
		}, ct).ConfigureAwait(false);
		var result = await Connection().RequestAsync("fs/read_text_file", new JsonObject {
			["sessionId"] = _sessionId,
			["path"] = path,
			["line"] = 1,
		}, ct).ConfigureAwait(false);
		Message("fs: " + result.GetProperty("content").GetString());
	}

	// Shipping agents run commands themselves and embed a terminal id the client never created.
	private void AgentOwnedTerminal() {
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = "agent-exec",
			["title"] = "echo hello",
			["kind"] = "execute",
			["status"] = "in_progress",
			["content"] = new JsonArray(new JsonObject {
				["type"] = "terminal",
				["terminalId"] = "agent-owned-terminal",
			}),
		});
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "agent-exec",
			["status"] = "completed",
			["rawOutput"] = "hello",
		});
		Message("agent terminal finished");
	}

	private async Task TerminalFailureAsync(CancellationToken ct) {
		await Connection().RequestAsync("terminal/create", new JsonObject {
			["sessionId"] = _sessionId,
			["command"] = Path.Combine(Path.GetTempPath(), "weavie-missing-terminal-command"),
			["args"] = new JsonArray(),
		}, ct).ConfigureAwait(false);
	}

	private async Task TerminalOutputAsync(bool nullOptionals, CancellationToken ct) {
		string executable = Environment.ProcessPath
			?? throw new InvalidOperationException("The fake ACP executable path is unavailable.");
		var request = new JsonObject {
			["sessionId"] = _sessionId,
			["command"] = executable,
			["args"] = new JsonArray("terminal-output"),
		};
		if (nullOptionals) {
			request["cwd"] = null;
			request["outputByteLimit"] = null;
		}
		var created = await Connection().RequestAsync("terminal/create", request, ct).ConfigureAwait(false);
		string terminalId = AcpJson.RequiredString(created, "terminalId", "terminal/create response");
		var exited = await Connection().RequestAsync("terminal/wait_for_exit", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, ct).ConfigureAwait(false);
		var output = await Connection().RequestAsync("terminal/output", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, ct).ConfigureAwait(false);
		await Connection().RequestAsync("terminal/release", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, ct).ConfigureAwait(false);
		string captured = AcpJson.RequiredString(output, "output", "terminal/output response");
		Message(
			$"terminal: stdout={captured.Contains("stdout-tail", StringComparison.Ordinal)}"
			+ $";stderr={captured.Contains("stderr-tail", StringComparison.Ordinal)}"
			+ $";exit={exited.GetProperty("exitCode").GetInt32()}");
	}

	private async Task TerminalCancellationAsync(CancellationToken ct) {
		string executable = Environment.ProcessPath
			?? throw new InvalidOperationException("The fake ACP executable path is unavailable.");
		var created = await Connection().RequestAsync("terminal/create", new JsonObject {
			["sessionId"] = _sessionId,
			["command"] = executable,
			["args"] = new JsonArray("terminal-hold"),
		}, ct).ConfigureAwait(false);
		string terminalId = AcpJson.RequiredString(created, "terminalId", "terminal/create response");
		using var cancellation = new CancellationTokenSource();
		var waiting = Connection().RequestAsync("terminal/wait_for_exit", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, cancellation.Token);
		await Connection().RequestAsync("terminal/output", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, ct).ConfigureAwait(false);
		cancellation.Cancel();
		try {
			await waiting.ConfigureAwait(false);
			throw new InvalidOperationException("The cancelled terminal wait completed successfully.");
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
		}
		await Connection().RequestAsync("terminal/kill", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, ct).ConfigureAwait(false);
		await Connection().RequestAsync("terminal/release", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = terminalId,
		}, ct).ConfigureAwait(false);
		Message("terminal wait cancelled; connection alive");
	}

	private async Task CancelBeforeDispatchAsync() {
		using var cancellation = new CancellationTokenSource();
		var request = Connection().RequestAsync("terminal/wait_for_exit", new JsonObject {
			["sessionId"] = _sessionId,
			["terminalId"] = "never-dispatched",
		}, cancellation.Token);
		cancellation.Cancel();
		try {
			await request.ConfigureAwait(false);
			throw new InvalidOperationException("The immediately cancelled request completed successfully.");
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
		}
		var result = await Connection().RequestAsync("fs/read_text_file", new JsonObject {
			["sessionId"] = _sessionId,
			["path"] = Path.Combine(Directory.GetCurrentDirectory(), "missing.txt"),
		}, CancellationToken.None).ConfigureAwait(false);
		Message("cancel-before-dispatch handled: " + result.GetProperty("content").GetString());
	}

	private void ContextResult(JsonElement prompt, string label) {
		bool guidance = prompt.EnumerateArray().Any(block => ResourceUri(block) == "weavie://instructions");
		bool selection = prompt.EnumerateArray().Any(block => ResourceUri(block)?.EndsWith("#selection", StringComparison.Ordinal) == true);
		Message($"{label}:guidance={guidance};selection={selection}");
	}

	private JsonObject SetMode(JsonElement parameters) {
		RequireSession(parameters);
		if (_fakeMode == "mirrored-mode") {
			throw AcpAdapterException.InvalidParams("The mirrored mode axis is written through set_config_option.");
		}
		_mode = AcpJson.RequiredString(parameters, "modeId", "session/set_mode");
		return Setup();
	}

	private async Task<JsonObject> SetConfigAsync(JsonElement parameters, CancellationToken ct) {
		RequireSession(parameters);
		string id = AcpJson.RequiredString(parameters, "configId", "session/set_config_option");
		var value = parameters.GetProperty("value");
		if (id == "model") {
			string model = value.GetString() ?? throw AcpAdapterException.InvalidParams("model must be a string");
			if (model == "beta") {
				File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "control-started"), string.Empty);
				await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
			}
			_model = model;
		} else if (id == "fast") _fast = value.GetBoolean();
		else if (id == "mode" && _fakeMode == "mirrored-mode") {
			_mode = value.GetString() ?? throw AcpAdapterException.InvalidParams("mode must be a string");
		} else throw AcpAdapterException.InvalidParams($"Unknown fake config '{id}'.");
		return Setup();
	}

	private void ReplayProgress(string text) => Update(new JsonObject {
		["sessionUpdate"] = "plan",
		["entries"] = new JsonArray(new JsonObject {
			["content"] = text,
			["status"] = "completed",
			["priority"] = "medium",
		}),
	});

	private void PlanDocument(string planId, string content) {
		if (!_supportsPlanUpdates) throw new InvalidOperationException("The client did not advertise plan updates.");
		Update(new JsonObject {
			["sessionUpdate"] = "plan_update",
			["plan"] = new JsonObject {
				["type"] = "markdown",
				["planId"] = planId,
				["content"] = content,
			},
		});
	}

	private void ItemPlanDocument() => Update(new JsonObject {
		["sessionUpdate"] = "plan_update",
		["plan"] = new JsonObject {
			["type"] = "items",
			["planId"] = "item-plan",
			["entries"] = new JsonArray(
				new JsonObject { ["content"] = "Inspect", ["status"] = "completed", ["priority"] = "medium" },
				new JsonObject { ["content"] = "Implement", ["status"] = "pending", ["priority"] = "high" }),
		},
	});

	private void FilePlanDocument() {
		string path = Path.Combine(Environment.CurrentDirectory, "file-plan.md");
		File.WriteAllText(path, "# File plan");
		Update(new JsonObject {
			["sessionUpdate"] = "plan_update",
			["plan"] = new JsonObject {
				["type"] = "file",
				["planId"] = "file-plan",
				["uri"] = new Uri(path).AbsoluteUri,
			},
		});
	}

	private void ExternalFilePlanDocument() => Update(new JsonObject {
		["sessionUpdate"] = "plan_update",
		["plan"] = new JsonObject {
			["type"] = "file",
			["planId"] = "external-file-plan",
			["uri"] = new Uri(Path.Combine(Environment.CurrentDirectory, "..", "outside-plan.md")).AbsoluteUri,
		},
	});

	private void RemovePlan(string planId) => Update(new JsonObject {
		["sessionUpdate"] = "plan_removed",
		["planId"] = planId,
	});

	// Shipping agents mirror one mode axis in both configOptions and the legacy modes block.
	private JsonObject Setup() {
		var setup = SetupCore();
		if (_fakeMode != "mirrored-mode") return setup;
		((JsonArray)setup["configOptions"]!).Insert(0, new JsonObject {
			["id"] = "mode",
			["name"] = "Mode",
			["category"] = "mode",
			["type"] = "select",
			["currentValue"] = _mode,
			["options"] = new JsonArray(
				new JsonObject { ["value"] = "default", ["name"] = "Default" },
				new JsonObject { ["value"] = "plan", ["name"] = "Plan" }),
		});
		return setup;
	}

	private JsonObject SetupCore() => new() {
		["configOptions"] = new JsonArray(
			new JsonObject {
				["id"] = "model",
				["name"] = "Model",
				["category"] = "model",
				["type"] = "select",
				["currentValue"] = _model,
				["options"] = new JsonArray(
					new JsonObject {
						["group"] = "stable",
						["name"] = "Stable",
						["options"] = new JsonArray(
							new JsonObject { ["value"] = "alpha", ["name"] = "Alpha" }),
					},
					new JsonObject {
						["group"] = "preview",
						["name"] = "Preview",
						["options"] = new JsonArray(
							new JsonObject { ["value"] = "beta", ["name"] = "Beta" }),
					}),
			},
			new JsonObject {
				["id"] = "fast",
				["name"] = "Fast",
				["category"] = "model_config",
				["type"] = "boolean",
				["currentValue"] = _fast,
			}),
		["modes"] = new JsonObject {
			["currentModeId"] = _mode,
			["availableModes"] = new JsonArray(
				new JsonObject { ["id"] = "default", ["name"] = "Default" },
				new JsonObject { ["id"] = "plan", ["name"] = "Plan" }),
		},
	};

	private void RequireMcp(JsonElement parameters) {
		var servers = AcpJson.RequiredArray(parameters, "mcpServers", "session open");
		if (!servers.EnumerateArray().Any(server => AcpJson.OptionalString(server, "name") == "weavie"
			&& AcpJson.OptionalString(server, "type") == "http"
			&& server.TryGetProperty("headers", out var headers)
			&& headers.EnumerateArray().Any(header => AcpJson.OptionalString(header, "name") == "Authorization"
				&& AcpJson.OptionalString(header, "value")?.StartsWith("Bearer ", StringComparison.Ordinal) == true))) {
			throw AcpAdapterException.InvalidParams("Weavie HTTP MCP credentials are missing.");
		}
	}

	private static void RequireStdioMcp(JsonElement parameters) {
		var servers = AcpJson.RequiredArray(parameters, "mcpServers", "session open");
		if (!servers.EnumerateArray().Any(server => AcpJson.OptionalString(server, "name") == "weavie"
			&& AcpJson.OptionalString(server, "type") == "stdio"
			&& AcpJson.OptionalString(server, "command") is { } command
			&& Path.IsPathFullyQualified(command))) {
			throw AcpAdapterException.InvalidParams("Weavie stdio MCP launch is missing.");
		}
	}

	private void RequireSession(JsonElement parameters) {
		string id = AcpJson.RequiredString(parameters, "sessionId", "fake session request");
		if (id != _sessionId) throw AcpAdapterException.InvalidParams($"Unknown fake session '{id}'.");
	}

	private void Message(string text) => Update(new JsonObject {
		["sessionUpdate"] = "agent_message_chunk",
		["messageId"] = Guid.NewGuid().ToString("N"),
		["content"] = Text(text),
	});

	private void Update(JsonObject update) => Connection().Notify("session/update", new JsonObject {
		["sessionId"] = _sessionId,
		["update"] = update,
	});

	private AcpAgentConnection Connection() =>
		_connection ?? throw new InvalidOperationException("Fake ACP is not attached.");

	private static JsonObject Text(string value) => new() { ["type"] = "text", ["text"] = value };

	private static JsonObject Resource(string uri, string text) => new() {
		["type"] = "resource",
		["resource"] = new JsonObject {
			["uri"] = uri,
			["mimeType"] = "text/plain",
			["text"] = text,
		},
	};

	private static string PromptText(JsonElement prompt) => string.Concat(prompt.EnumerateArray()
		.Where(block => AcpJson.OptionalString(block, "type") == "text")
		.Select(block => AcpJson.OptionalString(block, "text")));

	private static string? ResourceUri(JsonElement block) =>
		block.TryGetProperty("resource", out var resource) ? AcpJson.OptionalString(resource, "uri") : null;
}

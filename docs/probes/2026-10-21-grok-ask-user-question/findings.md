# Grok `_x.ai/ask_user_question` over ACP

Grok 1.0.62, `grok agent --no-leader stdio`, with `yoloMode: false` and no MCP servers in `session/new`. `probe_ask.py <repo> '<reply json>'` asks Grok to put one question to the user, answers the first `_x.ai/ask_user_question` with the given reply, and prints the tool updates.

## Request

Grok asks through a client request, not ACP's `elicitation/create`:

```json
{"jsonrpc": "2.0", "id": 0, "method": "_x.ai/ask_user_question",
 "params": {"sessionId": "01a15c7b-…", "toolCallId": "call-…-0", "mode": "default",
            "questions": [{"question": "Which colour?",
                           "options": [{"label": "Red", "description": "Red"}, {"label": "Blue", "description": "Blue"}],
                           "multiSelect": null}]}}
```

A client that does not implement the method answers `Method not found`, and the model sees the tool fail.

## Reply

`{"outcome": …, "answers": {…}}`. The reply was worked out from Grok's own deserialisation errors:

| Reply | Grok's tool result |
|---|---|
| `{"outcome": {"outcome": "cancelled"}}` | `invalid type: map, expected variant identifier` |
| `{"outcome": "zzz"}` | `unknown variant zzz, expected one of accepted, chat_about_this, skip_interview, cancelled` |
| `{"outcome": "accepted", "answers": {"zz": 1}}` | `data did not match any variant of untagged enum StringOrVec` |
| `{"outcome": "accepted", "answers": {"Which colour?": "Blue"}}` | completed, `User has answered your questions: "Which colour?"="Blue"`, and the model replied `you picked Blue` |
| `{"outcome": "cancelled"}` | completed, `User declined to answer the questions. Continue with the task using your best judgment, or ask different questions.` |

Each answer is keyed by the question's text and is a label, or a list of labels for a multi-select.

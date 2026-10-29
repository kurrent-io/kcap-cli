using System.Text;

namespace Capacitor.App.Tests.Unit;

/// Literal prompts and long variants covering code, paths, whitespace and harness markup.
static class ChatPromptCorpus {
    static readonly ChatPromptCase[] ShortPrompts = [
        new("P001", "unix-paths", "Inspect /tmp/demo/file.txt\nExplain the contents without changing it."),
        new("P002", "unix-paths", "/tmp/demo/file.txt\nExplain this path and its parent."),
        new("P003", "unix-paths", "Compare ./src/main.cs and ../shared/main.cs\nList differences."),
        new("P004", "unix-paths", "Inspect ~/Projects/My App/config.json\nKeep spaces in the path."),
        new("P005", "unix-paths", "Review /tmp/a'b/\"quoted\"/file.txt\nTreat this as a literal path."),
        new("P006", "unix-paths", "Explain /tmp/[draft]/{a,b}/file?.txt\nDo not expand glob characters."),
        new("P007", "unix-paths", "Compare /tmp/na\u00efve/\u65e5\u672c\u8a9e/\u0444\u0430\u0439\u043b.txt\nPreserve Unicode."),
        new("P008", "unix-paths", "Inspect file:///tmp/demo%20folder/a.txt\nExplain URL decoding."),
        new("P009", "windows-paths", "Inspect C:\\Users\\Example\\repo\\main.cs\nExplain the path."),
        new("P010", "windows-paths", "Inspect C:\\Program Files\\Demo\\app.exe\nKeep spaces."),
        new("P011", "windows-paths", "Review \\\\server\\share\\folder\\report.txt\nTreat it as a UNC path."),
        new("P012", "windows-paths", "Explain \\\\?\\C:\\very\\long\\path.txt\nPreserve the prefix."),
        new("P013", "windows-paths", "Compare C:/repo/src/app.ts and D:/archive/app.ts\nList differences."),
        new("P014", "windows-paths", "Explain %USERPROFILE%\\Documents\\notes.txt\nDo not expand it."),
        new("P015", "windows-paths", "Review .\\src\\a.cs and ..\\shared\\b.cs\nResolve both relative paths."),
        new("P016", "windows-paths", "Explain \\Device\\HarddiskVolume1\\repo\\a.txt\nTreat as literal text."),
        new("P017", "shell-one-line", "Explain this command:\nprintf \"%s\\n\" \"hello world\""),
        new("P018", "shell-one-line", "Explain this pipeline:\nrg -n \"TODO|FIXME\" src | sort | uniq -c"),
        new("P019", "shell-one-line", "Explain conditional execution:\ntest -f ./demo && printf yes || printf no"),
        new("P020", "shell-one-line", "Explain these redirections:\ncat < input.txt > output.txt 2> error.txt"),
        new("P021", "shell-one-line", "Explain command substitution without executing it:\nprintf \"%s\" \"$(pwd)\""),
        new("P022", "shell-one-line", "Explain the quoting:\nprintf '%s\\n' '$HOME' \"${USER:-example}\""),
        new("P023", "shell-one-line", "Explain the glob:\nfind ./demo -name \"*.cs\" -print0 | xargs -0 -n 1 echo"),
        new("P024", "shell-one-line", "Explain backticks as text:\nprintf \"%s\" `date +%F`"),
        new("P025", "shell-multiline", "Explain this script:\n#!/usr/bin/env bash\nset -euo pipefail\nprintf \"%s\\n\" \"hello\""),
        new("P026", "shell-multiline", "Explain this heredoc:\ncat <<'EOF'\n$HOME `pwd` $(date)\nEOF"),
        new("P027", "shell-multiline", "Explain this loop:\nfor p in ./src/*.cs; do\n  printf \"%s\\n\" \"$p\"\ndone"),
        new("P028", "shell-multiline", "Explain this function:\nhello() {\n  local name=\"$1\"\n  printf \"hello %s\\n\" \"$name\"\n}"),
        new("P029", "shell-multiline", "Explain this continuation:\nprintf \"%s\\n\" \\\n  \"first argument\" \\\n  \"second argument\""),
        new("P030", "shell-multiline", "Explain this case:\ncase \"$mode\" in\n  dev) echo local ;;\n  *) echo other ;;\nesac"),
        new("P031", "shell-multiline", "Explain this PowerShell:\n$items = Get-ChildItem -Path \"C:\\Demo\"\n$items | Where-Object { $_.Length -gt 10 }"),
        new("P032", "shell-multiline", "Explain this batch file:\n@echo off\nset \"ROOT=C:\\Demo Folder\"\necho %ROOT%"),
        new("P033", "code", "Review this Python:\n```python\ndef hello(name):\n    return f\"hello {name}\"\n```"),
        new("P034", "code", "Review this C#:\n```csharp\nvar p = @\"C:\\Demo\\a.txt\";\nConsole.WriteLine(p);\n```"),
        new("P035", "code", "Review this JavaScript:\n```js\nconst path = `/tmp/${name}/file`;\nconsole.log(path);\n```"),
        new("P036", "code", "Review this SQL:\n```sql\nSELECT id, count(*)\nFROM events\nGROUP BY id;\n```"),
        new("P037", "code", "Review this Rust:\n```rust\nfn main() {\n    println!(\"hello\");\n}\n```"),
        new("P038", "code", "Explain this diff:\n```diff\n--- a/file\n+++ b/file\n@@ -1 +1 @@\n-old\n+new\n```"),
        new("P039", "code", "Review this Dockerfile:\nFROM alpine:3\nWORKDIR /app\nCOPY . .\nCMD [\"/bin/sh\"]"),
        new("P040", "code", "Explain this Makefile:\nbuild:\n\tprintf \"%s\\n\" \"building\"\n\tprintf done"),
        new("P041", "structured-data", "Review this JSON:\n{\n  \"path\": \"C:\\\\Demo\\\\a.txt\",\n  \"command\": \"echo hello\"\n}"),
        new("P042", "structured-data", "Review this YAML:\nsteps:\n  - run: |\n      echo first\n      echo second"),
        new("P043", "structured-data", "Review this TOML:\n[tool.demo]\npath = \"/tmp/demo\"\nargs = [\"a\", \"b\"]"),
        new("P044", "structured-data", "Review this XML:\n<project>\n  <path>/tmp/demo</path>\n  <command>echo hi</command>\n</project>"),
        new("P045", "structured-data", "Explain this CSV:\nname,path\n\"a,b\",\"/tmp/a b\"\n\"c\",\"C:\\Demo\""),
        new("P046", "structured-data", "Explain this TSV:\nname\tpath\nalpha\t/tmp/a\nbeta\t/tmp/b"),
        new("P047", "structured-data", "Review this HTTP request:\nPOST /api/demo HTTP/1.1\nContent-Type: application/json\n\n{\"message\":\"hi\"}"),
        new("P048", "structured-data", "Review these environment values:\nDEMO_PATH=\"/tmp/demo folder\"\nDEMO_CMD='echo $HOME'"),
        new("P049", "markdown", "# Task\n\nInspect the paths below.\n\n- /tmp/a\n- /tmp/b"),
        new("P050", "markdown", "Explain inline code `echo \"$HOME\"`.\nThen inspect `./src/app.cs`."),
        new("P051", "markdown", "Review this table:\n| Path | Command |\n| --- | --- |\n| /tmp/a | echo hi |"),
        new("P052", "markdown", "> quoted command\n> echo hello\n\nExplain the quote."),
        new("P053", "markdown", "1. Inspect ./src\n2. Read ./README.md\n3. Report findings."),
        new("P054", "markdown", "Review these tasks:\n- [ ] Read /tmp/a\n- [x] Explain `echo hi`"),
        new("P055", "markdown", "Explain this nested fence:\n````md\n```bash\necho hello\n```\n````"),
        new("P056", "markdown", "Explain this link and HTML:\n[local](file:///tmp/demo)\n<div>sample &amp; literal</div>"),
        new("P057", "unicode", "\u041e\u0431\u044f\u0441\u043d\u0438 \u0442\u043e\u0437\u0438 \u043f\u044a\u0442:\n/tmp/\u043f\u0440\u0438\u043c\u0435\u0440/\u0444\u0430\u0439\u043b.txt"),
        new("P058", "unicode", "\u3053\u306e\u30b3\u30de\u30f3\u30c9\u3092\u8aac\u660e\u3057\u3066\u304f\u3060\u3055\u3044:\nprintf \"%s\" \"\u3053\u3093\u306b\u3061\u306f\""),
        new("P059", "unicode", "\u0627\u0634\u0631\u062d \u0647\u0630\u0627 \u0627\u0644\u0645\u0633\u0627\u0631:\n/tmp/\u0645\u062b\u0627\u0644/file.txt"),
        new("P060", "unicode", "Explain emoji in paths:\n/tmp/\ud83e\uddea/\ud83d\udc69\ud83c\udffd\u200d\ud83d\udcbb/file.txt"),
        new("P061", "unicode", "Compare decomposed e\u0301 and composed \u00e9:\n/tmp/cafe\u0301\n/tmp/caf\u00e9"),
        new("P062", "unicode", "Explain smart punctuation:\n\u201cquoted\u201d \u2018single\u2019 \u2014 dash \u2026 ellipsis"),
        new("P063", "unicode", "Explain invisible characters:\nzero\u200bwidth\u00a0space\u2060joiner"),
        new("P064", "unicode", "Explain mathematical text:\n\ud835\udc9c = {\u03b1, \u03b2, \u03b3}\n\u2200x \u2208 \ud835\udc9c: x \u2260 \u2205"),
        new("P065", "whitespace", "  Explain these paths:  \n  /tmp/a  \n  /tmp/b  "),
        new("P066", "whitespace", "Explain CRLF:\r\n/tmp/a\r\n/tmp/b\r\n"),
        new("P067", "whitespace", "Explain CR-only:\r/tmp/a\r/tmp/b\r"),
        new("P068", "whitespace", "Mixed endings:\r\nfirst\nsecond\rthird\r\n"),
        new("P069", "whitespace", "Explain tabs:\n\tif true; then\n\t\techo hi\n\tfi"),
        new("P070", "whitespace", "\n\nExplain blank lines.\n\n/tmp/a\n\n\n/tmp/b\n\n"),
        new("P071", "whitespace", "Explain final-newline handling:\necho hello\n"),
        new("P072", "whitespace", "Explain two final newlines:\necho hello\n\n"),
        new("P073", "literal-harness-tags", "Explain this literal tag:\n<system-reminder>keep this user data</system-reminder>\nDo not discard it."),
        new("P074", "literal-harness-tags", "Explain this example:\n```xml\n<command-name>/review</command-name>\n<command-args>src/a.cs</command-args>\n```"),
        new("P075", "literal-harness-tags", "Explain this XML:\n<command-message>literal command documentation</command-message>\nKeep the text."),
        new("P076", "literal-harness-tags", "Explain stdout documentation:\n<local-command-stdout>literal output</local-command-stdout>\nKeep it visible."),
        new("P077", "literal-harness-tags", "Explain caveat documentation:\n<local-command-caveat>literal caveat</local-command-caveat>\nKeep it visible."),
        new("P078", "literal-harness-tags", "<task-notification>\nThis is a user-written schema example.\n</task-notification>"),
        new("P079", "literal-harness-tags", "<agent-message from=\"example\">\nThis is a user-written schema example.\n</agent-message>"),
        new("P080", "literal-harness-tags", "<bash-stdout>\nThis is a user-written example, not actual shell output.\n</bash-stdout>"),
        new("P081", "bash-tag-collisions", "<bash-input>\necho literal\n</bash-input>"),
        new("P082", "bash-tag-collisions", "Explain this snippet:\n<bash-input>echo literal</bash-input>\nKeep it as prose."),
        new("P083", "bash-tag-collisions", "Explain a nested shell tag:\n<bash-input>printf \"</bash-input>\"</bash-input>"),
        new("P084", "bash-tag-collisions", "Explain output tags:\n<bash-stdout>ok</bash-stdout>\n<bash-stderr>warn</bash-stderr>\nThese are examples."),
        new("P085", "bash-tag-collisions", "<bash-stderr>\nA literal error example.\n</bash-stderr>"),
        new("P086", "bash-tag-collisions", "Explain reminder nesting:\n<system-reminder>outer <system-reminder>inner</system-reminder> tail</system-reminder>"),
        new("P087", "bash-tag-collisions", "Explain an unclosed example:\n<system-reminder>literal remainder"),
        new("P088", "bash-tag-collisions", "Explain a namespaced tag:\n<x:system-reminder>literal data</x:system-reminder>"),
        new("P089", "paste-tag-collisions", "Explain this literal paste schema:\n<pasted_content id=\"demo\">\nuser data\n</pasted_content id=\"demo\">"),
        new("P090", "paste-tag-collisions", "Explain the one-line syntax:\n<pasted_content id=\"demo\">inline</pasted_content id=\"demo\">"),
        new("P091", "paste-tag-collisions", "Explain the mismatched ids:\n<pasted_content id=\"a\">\nuser data\n</pasted_content id=\"b\">"),
        new("P092", "paste-tag-collisions", "Explain the missing close:\n<pasted_content id=\"demo\">\nuser data"),
        new("P093", "paste-tag-collisions", "Explain CRLF paste syntax:\r\n<pasted_content id=\"demo\">\r\nuser data\r\n</pasted_content id=\"demo\">"),
        new("P094", "paste-tag-collisions", "Explain multiple blocks:\n<pasted_content id=\"a\">\none\n</pasted_content id=\"a\">\n<pasted_content id=\"b\">\ntwo\n</pasted_content id=\"b\">"),
        new("P095", "paste-tag-collisions", "Explain nested blocks:\n<pasted_content id=\"a\">\n<pasted_content id=\"b\">\ninside\n</pasted_content id=\"b\">\n</pasted_content id=\"a\">"),
        new("P096", "paste-tag-collisions", "Explain an empty id:\n<pasted_content id=\"\">\nuser data\n</pasted_content id=\"\">"),
        new("P097", "codex-prefix-collisions", "<environment_context>\nUser-written XML example with /tmp/demo.\n</environment_context>"),
        new("P098", "codex-prefix-collisions", "# AGENTS.md instructions\nExplain this sample instruction file.\nRead ./src only."),
        new("P099", "codex-prefix-collisions", "<turn_aborted>\nExplain this literal tag.\n</turn_aborted>"),
        new("P100", "codex-prefix-collisions", "<user_instructions>\nReview these example instructions.\n</user_instructions>"),
        new("P101", "codex-prefix-collisions", "<permissions instructions>\nExplain this sample permission schema.\n</permissions instructions>"),
        new("P102", "codex-prefix-collisions", "Explain this prefix:\n<environment_context>literal</environment_context>"),
        new("P103", "codex-prefix-collisions", "  # AGENTS.md instructions\nExplain this indented sample.\nKeep it visible."),
        new("P104", "codex-prefix-collisions", "```xml\n<user_instructions>literal</user_instructions>\n```\nExplain the code block."),
        new("P113", "bang-and-slash", "! printf \"%s\\n\" \"hello\"\n# second line"),
        new("P114", "bang-and-slash", "!\nprintf \"%s\\n\" \"hello\"\nprintf done"),
        new("P115", "bang-and-slash", "! cat <<'EOF'\n/tmp/a b\nEOF"),
        new("P116", "bang-and-slash", "/review\nInspect ./src/main.cs\nReport findings."),
        new("P117", "bang-and-slash", "/review\t\tInspect ./src\nKeep the second line."),
        new("P118", "bang-and-slash", "/tmp/demo/file.txt\nThis is a path, not an intended slash command."),
        new("P119", "bang-and-slash", "Explain a bang in prose:\n!important is CSS terminology here."),
        new("P120", "bang-and-slash", "Explain slash notation:\n/review is shown as documentation, not invoked."),
        new("P121", "logs-and-controls", "Explain this stack trace:\nSystem.Exception: sample\n  at Demo.Run() in C:\\repo\\Program.cs:line 42"),
        new("P122", "logs-and-controls", "Explain these logs:\n2026-10-19T12:00:00Z INFO path=/tmp/a\n2026-10-19T12:00:01Z ERROR command=\"echo hi\""),
        new("P123", "logs-and-controls", "Explain JSON lines:\n{\"path\":\"/tmp/a\"}\n{\"command\":\"echo hi\"}"),
        new("P124", "logs-and-controls", "Explain an ANSI log (literal control bytes):\n\u001b[31mERROR\u001b[0m\n/tmp/a"),
        new("P125", "logs-and-controls", "Explain bracketed paste markers (literal bytes):\n\u001b[200~echo hi\u001b[201~\nKeep them as data."),
        new("P126", "logs-and-controls", "Explain NUL in imported text:\nleft\u0000right\nKeep surrounding text."),
        new("P127", "logs-and-controls", "Explain terminal controls:\nleft\bX\tcolumn\nnext\u0007line"),
        new("P128", "logs-and-controls", "Review an attachment-looking literal:\n[Attached files: .attached/example/a.png]\nThis is documentation."),
    ];

    static readonly int[] Sizes = [4095, 4096, 4097, 16384, 65536, 262143, 262144, 262145];

    public static IReadOnlyList<ChatPromptCase> All() {
        var original = ShortPrompts.ToList();
        for (var i = 0; i < Sizes.Length; i++) {
            var size = Sizes[i];
            var prefix = $"Explain this {size}-byte ASCII prompt with a path and command.\n/tmp/demo/file.txt\nprintf hello\n";
            original.Add(new($"P{105 + i:000}", "size-boundaries", prefix + new string('x', size - prefix.Length)));
        }
        original.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var result = new List<ChatPromptCase>(original);
        foreach (var prompt in original) {
            var repeats = Math.Max(1, 16384 / Encoding.UTF8.GetByteCount(prompt.Text));
            var text = new StringBuilder(prompt.Text);
            for (var i = 0; i < repeats; i++) text.Append("\n\n").Append(prompt.Text);
            text.Append("\nEnd of long prompt ").Append(prompt.Id).Append('.');
            result.Add(new(prompt.Id.Replace('P', 'L'), prompt.Category, text.ToString()));
        }
        return result;
    }
}

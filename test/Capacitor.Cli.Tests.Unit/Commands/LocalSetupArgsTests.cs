using Capacitor.Cli.Core.Setup.Intelligence;
using Capacitor.Cli.Commands;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.Commands;

internal sealed class LocalSetupArgsTests {
    [Test]
    [Arguments("--server-url", "https://acme.kcap.ai")]
    [Arguments("--server-url=https://acme.kcap.ai", null)]
    [Arguments("--discover", null)]
    [Arguments("--org", "Acme")]
    [Arguments("--slug", "acme")]
    [Arguments("--tier", "team")]
    [Arguments("--invite-code", "abc")]
    [Arguments("--device", null)]
    [Arguments("--github", null)]
    [Arguments("--muse-assistant", null)]
    public async Task A_conflicting_flag_is_refused_by_name(string flag, string? value) {
        string[] args = value is null ? ["setup", "--local", flag] : ["setup", "--local", flag, value];

        var (parsed, error) = LocalSetupArgs.Parse(args, kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains(flag.Split('=')[0]);
    }

    [Test]
    public async Task A_positional_tenant_is_refused() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "acme", "--local"], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("acme");
    }

    [Test]
    [Arguments("--single-user", "acme")]
    [Arguments("--yes", "example-tenant")]
    [Arguments("--no-prompt", "acme")]
    public async Task A_positional_name_after_a_flag_is_refused(string flag, string name) {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", flag, name], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains($"'{name}' names a server");
    }

    [Test]
    [Arguments("--daemon-name", "x")]
    [Arguments("--plugin-scope", "user")]
    [Arguments("--default-visibility", "private")]
    [Arguments("--use-provider-api-key", "true")]
    public async Task A_pass_through_flag_value_is_not_a_positional_name(string flag, string value) {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", flag, value, "--single-user"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: false, SingleUser: true, Yes: false, NoPrompt: false));
    }

    [Test]
    public async Task A_set_KCAP_URL_is_refused() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local"], kcapUrlEnv: "https://acme.kcap.ai");

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("KCAP_URL");
    }

    [Test]
    public async Task An_empty_KCAP_URL_is_not_a_conflict() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local"], kcapUrlEnv: "");

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: false, SingleUser: false, Yes: false, NoPrompt: false));
    }

    [Test]
    public async Task Json_refuses_a_flag_that_only_the_terminal_steps_read() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--default-visibility", "private"], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("--default-visibility");
    }

    [Test]
    public async Task Json_implies_no_prompt_and_accepts_its_own_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--json", "--single-user", "--yes", "--no-update-check"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: true, SingleUser: true, Yes: true, NoPrompt: true));
    }

    /// <summary>Without --no-prompt, --json is the desktop app's install-only run and keeps refusing these.</summary>
    [Test]
    public async Task Json_with_no_prompt_runs_every_step_and_keeps_their_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--json", "--no-prompt", "--single-user", "--yes", "--default-visibility", "private", "--skip-import", "--intelligence", "none"],
            kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: true, SingleUser: true, Yes: true, NoPrompt: true, WholeRun: true, Intelligence: IntelligenceAnswer.None));
    }

    [Test]
    public async Task The_terminal_arm_keeps_the_later_steps_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--no-prompt", "--default-visibility", "private", "--skip-import", "--intelligence", "none"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: false, SingleUser: false, Yes: false, NoPrompt: true, Intelligence: IntelligenceAnswer.None));
    }

    [Test]
    public async Task Json_accepts_the_embedding_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--json", "--single-user", "--embedding-model", "nomic-embed-text:v1.5"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed!.Embeddings).IsEqualTo(new LocalEmbeddingAnswer.Ollama("nomic-embed-text:v1.5"));

        var (skip, _) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--no-embeddings"], kcapUrlEnv: null);
        await Assert.That(skip!.Embeddings).IsEqualTo(new LocalEmbeddingAnswer.Skip());

        var (builtin, builtinError) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--builtin-embeddings"], kcapUrlEnv: null);
        await Assert.That(builtinError).IsNull();
        await Assert.That(builtin!.Embeddings).IsEqualTo(new LocalEmbeddingAnswer.Builtin());
    }

    [Test]
    public async Task The_embedding_model_takes_the_equals_form_too() {
        var (terminal, _) = LocalSetupArgs.Parse(["setup", "--local", "--embedding-model=nomic-embed-text"], kcapUrlEnv: null);
        var (json, error) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--embedding-model=nomic-embed-text"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(terminal!.Embeddings).IsEqualTo(new LocalEmbeddingAnswer.Ollama("nomic-embed-text"));
        await Assert.That(json!.Embeddings).IsEqualTo(new LocalEmbeddingAnswer.Ollama("nomic-embed-text"));
    }

    [Test]
    public async Task An_empty_equals_form_is_refused() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--embedding-model="], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("--embedding-model needs an Ollama model name");
    }

    [Test]
    public async Task No_embedding_flag_leaves_the_choice_to_the_default() {
        var (parsed, _) = LocalSetupArgs.Parse(["setup", "--local", "--no-prompt", "--intelligence", "none"], kcapUrlEnv: null);

        await Assert.That(parsed!.Embeddings).IsEqualTo(new LocalEmbeddingAnswer.Default());
    }

    [Test]
    [Arguments("--embedding-model")]
    [Arguments("--embedding-model", "--yes")]
    [Arguments("--embedding-model", "bad name")]
    public async Task The_embedding_model_flag_needs_a_model_name(params string[] flags) {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", .. flags], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("--embedding-model needs an Ollama model name");
    }

    [Test]
    [Arguments("--no-embeddings", "--embedding-model", "mxbai-embed-large")]
    [Arguments("--builtin-embeddings", "--embedding-model", "mxbai-embed-large")]
    [Arguments("--builtin-embeddings", "--no-embeddings")]
    public async Task Two_embedding_answers_contradict_each_other(params string[] flags) {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", .. flags], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("contradict");
    }

    /// <summary>The desktop app's install-only run passes it, so --json must accept it.</summary>
    [Test]
    public async Task Json_accepts_the_windows_service_flag_on_windows() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--single-user", "--yes", "--windows-service"], kcapUrlEnv: null,
            isWindows: true);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: true, SingleUser: true, Yes: true, NoPrompt: true, WindowsService: true));
    }

    [Test]
    public async Task The_windows_service_flag_is_refused_elsewhere() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--windows-service"], kcapUrlEnv: null, isWindows: false);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("--windows-service");
    }

    [Test]
    public async Task A_run_that_cannot_ask_needs_the_intelligence_answer() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--no-prompt", "--skip-import"], kcapUrlEnv: null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains("--intelligence");
    }

    [Test]
    public async Task The_desktop_apps_install_only_run_is_not_asked_for_it() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--json", "--single-user", "--yes"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed!.Intelligence).IsNull();
    }

    [Test]
    public async Task The_intelligence_flags_parse() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--no-prompt", "--intelligence", "openai", "--intelligence-model=gpt-5", "--no-import-outcomes"],
            kcapUrlEnv: null, variable: name => name == "OPENAI_API_KEY" ? "sk" : null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed!.Intelligence!.Token).IsEqualTo("openai");
        await Assert.That(parsed.IntelligenceModel).IsEqualTo("gpt-5");
        await Assert.That(parsed.ImportOutcomes == false).IsTrue();
    }

    [Test]
    [Arguments(new[] { "--intelligence", "gemini" }, "'gemini' is not an answer")]
    [Arguments(new[] { "--intelligence" }, "--intelligence needs")]
    [Arguments(new[] { "--intelligence", "claude", "--intelligence-model", "x" }, "--intelligence-model is for a provider")]
    [Arguments(new[] { "--intelligence", "claude", "--import-outcomes", "--no-import-outcomes" }, "contradict")]
    [Arguments(new[] { "--intelligence", "anthropic" }, "ANTHROPIC_API_KEY")]
    public async Task Intelligence_flags_that_cannot_work_are_refused(string[] flags, string reason) {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--no-prompt", .. flags], kcapUrlEnv: null, variable: _ => null);

        await Assert.That(parsed).IsNull();
        await Assert.That(error).Contains(reason);
    }

    [Test]
    public async Task An_interactive_run_may_name_a_provider_without_its_key_in_the_environment() {
        var (parsed, error) = LocalSetupArgs.Parse(["setup", "--local", "--intelligence", "cohere"], kcapUrlEnv: null, variable: _ => null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed!.Intelligence!.Vendor).IsEqualTo(Capacitor.Cli.Core.Setup.Intelligence.IntelligenceVendor.Cohere);
    }
}

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
            ["setup", "--local", "--json", "--no-prompt", "--single-user", "--yes", "--default-visibility", "private", "--skip-import"],
            kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: true, SingleUser: true, Yes: true, NoPrompt: true, WholeRun: true));
    }

    [Test]
    public async Task The_terminal_arm_keeps_the_later_steps_flags() {
        var (parsed, error) = LocalSetupArgs.Parse(
            ["setup", "--local", "--no-prompt", "--default-visibility", "private", "--skip-import"], kcapUrlEnv: null);

        await Assert.That(error).IsNull();
        await Assert.That(parsed).IsEqualTo(new LocalSetupArgs(Json: false, SingleUser: false, Yes: false, NoPrompt: true));
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
        var (parsed, _) = LocalSetupArgs.Parse(["setup", "--local", "--no-prompt"], kcapUrlEnv: null);

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
}

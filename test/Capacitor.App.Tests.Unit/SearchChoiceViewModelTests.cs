using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SearchChoiceViewModelTests {
    [Test]
    public async Task Keyword_search_does_not_require_a_valid_model_and_preserves_the_custom_choice() {
        var vm = new SearchChoiceViewModel { UseOllama = true, UseOther = true, OtherModel = "bad model" };
        await Assert.That(vm.Valid).IsFalse();
        vm.UseKeyword = true;
        await Assert.That(vm.Valid).IsTrue();
        await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.KeywordOnly);
        vm.OtherModel = "nomic-embed-text";
        vm.UseOllama = true;
        await Assert.That(vm.Choice.Model).IsEqualTo("nomic-embed-text");
    }

    [Test]
    public async Task A_fresh_choice_recommends_ollama_when_it_runs_and_the_builtin_model_otherwise_without_a_flag() {
        await AvaloniaSession.DispatchAsync(async () => {
            var running = false;
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(running), () => false);
            vm.Prefill(null);
            vm.OfferBuiltin(Offered);
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.UseBuiltin).IsTrue();
            await Assert.That(vm.OllamaEnabled).IsFalse();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Builtin));
            await Assert.That(vm.Choice.Args).IsEmpty();

            running = true;
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.UseOllama).IsTrue();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Default));
            await Assert.That(vm.Choice.Args).IsEmpty();
            await Assert.That(vm.ModelChoiceVisible).IsTrue();
            await Assert.That(vm.OllamaStatus).IsNull();
            return true;
        });
    }

    [Test]
    public async Task Picking_the_builtin_model_or_loading_it_as_recorded_names_it_with_its_flag() {
        await AvaloniaSession.DispatchAsync(async () => {
            var picked = new SearchChoiceViewModel(_ => Task.FromResult(true), () => true);
            picked.Prefill(null);
            picked.OfferBuiltin(Offered);
            await picked.CheckOllamaCommand.Execute().ToTask();
            picked.UseBuiltin = true;
            await picked.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(picked.Choice).IsEqualTo(LocalEmbeddingChoice.Builtin);
            await Assert.That(picked.Choice.Args).IsEquivalentTo(["--builtin-embeddings"]);

            var recorded = new SearchChoiceViewModel(_ => Task.FromResult(true), () => true);
            recorded.Prefill(LocalEmbeddingSetting.Builtin(new string('a', 64)));
            await recorded.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(recorded.UseBuiltin).IsTrue();
            await Assert.That(recorded.Choice).IsEqualTo(LocalEmbeddingChoice.Builtin);
            return true;
        });
    }

    static readonly LocalEmbeddingModelManifest Offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]);

    /// Without a built-in model to offer, the recommendation falls back to Ollama when it runs, else keyword search,
    /// still passing no flag; an offer that arrives brings it back.
    [Test]
    public async Task A_stack_offering_no_builtin_model_recommends_ollama_or_keyword_search() {
        await AvaloniaSession.DispatchAsync(async () => {
            var running = false;
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(running), () => false);
            vm.Prefill(null);
            vm.OfferBuiltin(null);
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.BuiltinEnabled).IsFalse();
            await Assert.That(vm.BuiltinStatus).IsEqualTo(SearchChoiceViewModel.BuiltinUnavailable);
            await Assert.That(vm.UseKeyword).IsTrue();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.KeywordOnly));
            await Assert.That(vm.Choice.Args).IsEmpty();

            running = true;
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Default));

            running = false;
            await vm.CheckOllamaCommand.Execute().ToTask();
            vm.OfferBuiltin(Offered);
            await Assert.That(vm.BuiltinEnabled).IsTrue();
            await Assert.That(vm.BuiltinStatus).IsNull();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Builtin));
            await Assert.That(vm.BuiltinDescription).Contains("(about 300 MB)");
            return true;
        });
    }

    /// Until the lookup answers, built-in is neither recommended nor selectable as a new choice, so nothing can ask a
    /// stack without a built-in model for it; a recorded built-in model stays selectable.
    [Test]
    public async Task Builtin_is_offered_as_a_new_choice_only_once_the_lookup_finds_it() {
        await AvaloniaSession.DispatchAsync(async () => {
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(false), () => false);
            vm.Prefill(null);
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.BuiltinEnabled).IsFalse();
            await Assert.That(vm.BuiltinStatus).IsEqualTo(SearchChoiceViewModel.BuiltinLookingUp);
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.KeywordOnly));

            vm.UseBuiltin = true;
            await Assert.That(vm.UseKeyword).IsTrue();
            await Assert.That(vm.Choice.Args).IsEmpty();

            vm.OfferBuiltin(Offered);
            await Assert.That(vm.BuiltinEnabled).IsTrue();
            await Assert.That(vm.BuiltinStatus).IsNull();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Builtin));

            var recorded = new SearchChoiceViewModel(_ => Task.FromResult(false), () => false);
            recorded.Prefill(LocalEmbeddingSetting.Builtin(new string('a', 64)));
            await Assert.That(recorded.BuiltinEnabled).IsTrue();
            await Assert.That(recorded.Choice).IsEqualTo(LocalEmbeddingChoice.Builtin);
            return true;
        });
    }

    [Test]
    public async Task A_builtin_pick_the_stack_cannot_honour_falls_back_but_a_recorded_one_stays() {
        await AvaloniaSession.DispatchAsync(async () => {
            var picked = new SearchChoiceViewModel(_ => Task.FromResult(false), () => false);
            picked.Prefill(LocalEmbeddingSetting.Declined);
            picked.OfferBuiltin(Offered);
            picked.UseBuiltin = true;
            picked.OfferBuiltin(null);
            await Assert.That(picked.UseKeyword).IsTrue();
            await Assert.That(picked.Choice.Args).IsEmpty();

            var recorded = new SearchChoiceViewModel(_ => Task.FromResult(false), () => false);
            recorded.Prefill(LocalEmbeddingSetting.Builtin(new string('a', 64)));
            recorded.OfferBuiltin(null);
            await Assert.That(recorded.UseBuiltin).IsTrue();
            await Assert.That(recorded.BuiltinEnabled).IsTrue();
            await Assert.That(recorded.Choice).IsEqualTo(LocalEmbeddingChoice.Builtin);
            return true;
        });
    }

    /// Both downloads name their size before anything is fetched.
    [Test]
    public async Task Each_search_by_meaning_option_names_its_download_size() {
        await Assert.That(SearchChoiceViewModel.BuiltinDetail).Contains("(about 320 MB)");
        await Assert.That(SearchChoiceViewModel.DefaultLabel).Contains("(about 670 MB)");
    }

    /// Installed-but-stopped needs a different action from not installed.
    [Test]
    public async Task The_status_names_the_missing_step() {
        await AvaloniaSession.DispatchAsync(async () => {
            var installed = false;
            var opener = new RecordingOpener();
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(false), () => installed, opener);
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.OllamaStatus).IsEqualTo("Ollama is not installed.");
            await Assert.That(vm.OllamaLinkVisible).IsTrue();
            await vm.OpenOllamaSiteCommand.Execute().ToTask();
            await Assert.That(opener.Opened).IsEquivalentTo([SearchChoiceViewModel.OllamaDownloadUrl]);

            installed = true;
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.OllamaStatus).IsEqualTo("Ollama is installed but not running. Open it.");
            await Assert.That(vm.OllamaLinkVisible).IsFalse();
            return true;
        });
    }

    [Test]
    public async Task A_pick_or_a_recorded_choice_is_not_overridden_by_a_later_check() {
        await AvaloniaSession.DispatchAsync(async () => {
            var picked = new SearchChoiceViewModel(_ => Task.FromResult(true), () => true);
            picked.Prefill(null);
            await picked.CheckOllamaCommand.Execute().ToTask();
            picked.UseKeyword = true;
            await picked.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(picked.UseKeyword).IsTrue();

            var running = true;
            var model = new SearchChoiceViewModel(_ => Task.FromResult(running), () => true);
            model.Prefill(null);
            await model.CheckOllamaCommand.Execute().ToTask();
            model.UseOther   = true;
            model.OtherModel = "nomic-embed-text";
            running = false;
            await model.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(model.Choice.Model).IsEqualTo("nomic-embed-text");

            var recorded = new SearchChoiceViewModel(_ => Task.FromResult(false), () => false);
            recorded.Prefill(LocalEmbeddingSetting.Ollama("nomic-embed-text"));
            await recorded.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(recorded.Choice.Model).IsEqualTo("nomic-embed-text");
            // Switching away from a recorded model while Ollama is stopped can still be undone.
            recorded.UseKeyword = true;
            await Assert.That(recorded.OllamaEnabled).IsTrue();
            return true;
        });
    }

    [Test]
    public async Task Search_modes_and_model_groups_are_independent_across_views() {
        await AvaloniaSession.DispatchAsync(async () => {
            var first = new SearchChoiceViewModel(_ => Task.FromResult(true), () => true);
            var second = new SearchChoiceViewModel(_ => Task.FromResult(true), () => true);
            var firstView = new SearchChoiceView { DataContext = first };
            var secondView = new SearchChoiceView { DataContext = second };
            var window = new Window { Content = new StackPanel { Children = { firstView, secondView } } };
            try {
                window.Show();
                // Each view checks Ollama once loaded; picks made before that would be followed over.
                Dispatcher.UIThread.RunJobs();
                firstView.FindControl<RadioButton>("SearchKeywordOption")!.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(first.UseKeyword).IsTrue();
                await Assert.That(second.UseOllama).IsTrue();
                secondView.FindControl<RadioButton>("SearchOtherOption")!.IsChecked = true;
                second.OtherModel = "nomic-embed-text";
                Dispatcher.UIThread.RunJobs();
                await Assert.That(first.Choice).IsEqualTo(LocalEmbeddingChoice.KeywordOnly);
                await Assert.That(second.Choice.Model).IsEqualTo("nomic-embed-text");
                firstView.FindControl<RadioButton>("SearchOllamaOption")!.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(first.Choice).IsEqualTo(LocalEmbeddingChoice.Default);
            } finally {
                window.Close();
            }
            return true;
        });
    }

    [Test]
    public async Task Returning_to_the_window_rechecks_a_missing_ollama() {
        var useMeaning = await AvaloniaSession.DispatchAsync(async () => {
            var running = false;
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(running), () => false);
            vm.Prefill(null);
            var window = new Window { Content = new SearchChoiceView { DataContext = vm } };
            var other = new Window();
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                other.Show();
                other.Activate();
                Dispatcher.UIThread.RunJobs();

                running = true;
                window.Activate();
                Dispatcher.UIThread.RunJobs();
                await Task.Yield();
                return vm.UseOllama;
            } finally {
                other.Close();
                window.Close();
            }
        });

        await Assert.That(useMeaning).IsTrue();
    }
}

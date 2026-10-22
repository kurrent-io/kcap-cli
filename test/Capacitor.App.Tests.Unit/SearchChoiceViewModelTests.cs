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
        var vm = new SearchChoiceViewModel { UseMeaning = true, UseOther = true, OtherModel = "bad model" };
        await Assert.That(vm.Valid).IsFalse();
        vm.UseKeyword = true;
        await Assert.That(vm.Valid).IsTrue();
        await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.KeywordOnly);
        vm.OtherModel = "nomic-embed-text";
        vm.UseMeaning = true;
        await Assert.That(vm.Choice.Model).IsEqualTo("nomic-embed-text");
    }

    [Test]
    public async Task A_fresh_choice_follows_whether_ollama_is_running() {
        await AvaloniaSession.DispatchAsync(async () => {
            var running = false;
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(running), () => false);
            vm.Prefill(null);
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.UseKeyword).IsTrue();
            await Assert.That(vm.MeaningEnabled).IsFalse();

            running = true;
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.Choice).IsEqualTo(LocalEmbeddingChoice.Default);
            await Assert.That(vm.ModelChoiceVisible).IsTrue();
            await Assert.That(vm.OllamaStatus).IsNull();
            return true;
        });
    }

    /// Installed-but-stopped needs a different action from not installed.
    [Test]
    public async Task The_status_names_the_missing_step() {
        await AvaloniaSession.DispatchAsync(async () => {
            var installed = false;
            var vm = new SearchChoiceViewModel(_ => Task.FromResult(false), () => installed);
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.OllamaStatus).StartsWith("Needs Ollama");

            installed = true;
            await vm.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(vm.OllamaStatus).StartsWith("Ollama is installed but not running.");
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

            var recorded = new SearchChoiceViewModel(_ => Task.FromResult(false), () => false);
            recorded.Prefill(new LocalEmbeddingSetting("nomic-embed-text"));
            await recorded.CheckOllamaCommand.Execute().ToTask();
            await Assert.That(recorded.Choice.Model).IsEqualTo("nomic-embed-text");
            await Assert.That(recorded.MeaningEnabled).IsTrue();
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
                await Assert.That(second.UseMeaning).IsTrue();
                secondView.FindControl<RadioButton>("SearchOtherOption")!.IsChecked = true;
                second.OtherModel = "nomic-embed-text";
                Dispatcher.UIThread.RunJobs();
                await Assert.That(first.Choice).IsEqualTo(LocalEmbeddingChoice.KeywordOnly);
                await Assert.That(second.Choice.Model).IsEqualTo("nomic-embed-text");
                firstView.FindControl<RadioButton>("SearchMeaningOption")!.IsChecked = true;
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
                return vm.UseMeaning;
            } finally {
                other.Close();
                window.Close();
            }
        });

        await Assert.That(useMeaning).IsTrue();
    }
}

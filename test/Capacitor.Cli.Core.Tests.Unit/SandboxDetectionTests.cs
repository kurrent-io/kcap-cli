namespace Capacitor.Cli.Core.Tests.Unit;

public class SandboxDetectionTests {
    [TempDir] public required TempDir Root { get; init; }

    static string? NoEnv(string _) => null;

    const string HostMounts =
        "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
        "25 22 0:5 / /proc rw,nosuid - proc proc rw\n";

    bool IsLikely(string path, Func<string, string?>? env = null) =>
        SandboxDetection.IsLikely(path, env ?? NoEnv, Root.Path);

    void Write(string mountinfo, string cgroup = "0::/\n") {
        Root.CreateFile(Path.Combine("proc", "self", "mountinfo"), mountinfo);
        Root.CreateFile(Path.Combine("proc", "1", "cgroup"), cgroup);
    }

    [Test]
    public async Task A_plain_host_is_not_a_sandbox() {
        Write(HostMounts);

        await Assert.That(IsLikely("/home/me/repo")).IsFalse();
    }

    [Test]
    [Arguments("SANDBOX_NAME")]
    [Arguments("container")]
    public async Task A_sandbox_environment_variable_is_enough(string variable) {
        Write(HostMounts);

        await Assert.That(IsLikely("/home/me/repo", v => v == variable ? "x" : null)).IsTrue();
    }

    [Test]
    public async Task A_docker_cgroup_is_enough() {
        Write(HostMounts, "0::/docker/55bdbfa1a767\n");

        await Assert.That(IsLikely("/home/me/repo")).IsTrue();
    }

    [Test]
    public async Task A_dockerenv_marker_is_enough() {
        Write(HostMounts);
        Root.CreateFile(".dockerenv");

        await Assert.That(IsLikely("/home/me/repo")).IsTrue();
    }

    /// <summary>The Docker sandbox the detection was built against: cgroup v2's bare root and no
    /// marker files, with the repository on a virtiofs share.</summary>
    [Test]
    public async Task A_repository_on_a_virtiofs_share_is_enough() {
        Write(HostMounts + "72 22 0:52 /d/ai /d/ai rw,nosuid,nodev,relatime - virtiofs host rw\n");

        await Assert.That(IsLikely("/d/ai/git/repo/.git")).IsTrue();
        await Assert.That(IsLikely("/home/me/repo/.git")).IsFalse();
    }

    [Test]
    public async Task The_longest_mount_point_decides() {
        const string mounts =
            "22 1 0:52 / / rw - virtiofs host rw\n" +
            "30 22 8:1 / /home rw - ext4 /dev/sda1 rw\n";

        await Assert.That(SandboxDetection.OnSharedMount("/home/me/repo", mounts)).IsFalse();
        await Assert.That(SandboxDetection.OnSharedMount("/srv/repo", mounts)).IsTrue();
    }

    [Test]
    public async Task A_mount_point_only_contains_paths_below_it() {
        const string mounts = "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n30 22 0:52 / /d rw - 9p host rw\n";

        await Assert.That(SandboxDetection.OnSharedMount("/data/repo", mounts)).IsFalse();
        await Assert.That(SandboxDetection.OnSharedMount("/d/repo", mounts)).IsTrue();
    }

    [Test]
    public async Task A_fuse_subtype_and_an_escaped_mount_point_are_read() {
        const string mounts = "22 1 8:1 / / rw - ext4 /dev/sda1 rw\n30 22 0:52 / /Users/me/My\\040Code rw shared:4 - fuse.grpcfuse grpcfuse rw\n";

        await Assert.That(SandboxDetection.OnSharedMount("/Users/me/My Code/repo", mounts)).IsTrue();
    }
}

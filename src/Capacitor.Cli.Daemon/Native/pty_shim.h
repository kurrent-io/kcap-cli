#ifndef PTY_SHIM_H
#define PTY_SHIM_H

#include <sys/types.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// A GENUINELY OPAQUE handle — this header exposes no fields. The shim owns everything a
// plan references (exec fd, paths, argv strings) for the plan's whole life; the only ways
// to touch a plan are through the functions declared here.
typedef struct pty_exec_plan pty_exec_plan;

// Startup capability probe (call once; cache the result and pass it into every
// pty_preflight call — no hidden global state). Uses the RAW syscall (bypasses any glibc
// execveat wrapper, since the wrapper only exists from glibc 2.34 — this keeps the floor a
// KERNEL floor, not a libc floor). Returns 1 if execveat(AT_EMPTY_PATH) is usable on this
// kernel, 0 otherwise (a test build may force 0 to exercise the <3.19/no-fd-exec fallback
// without needing an actual legacy kernel).
int pty_probe_execveat(void);

// Resolve + classify a launch, parent-side, pre-fork. Never execs. Returns 0 on success
// (*out_plan populated), -1 only when the plan itself cannot be constructed at all (e.g.
// exe_abs_path does not exist) — every other failure mode degrades to an uncontained plan
// rather than returning -1 (see pty_shim.c for the full decision tree).
int pty_preflight(const char* exe_abs_path, char* const orig_argv[], char* const envp[],
                   int execveat_supported, pty_exec_plan** out_plan);

// 1 = the plan is proven non-privileged and will use execveat(fd) (contained); 0 = the plan
// falls back to a normal execve(path) (uncontained — caller logs a warning, launch proceeds
// regardless).
int pty_plan_contained(const pty_exec_plan* plan);

// Frees every string/argv/fd a plan owns and sets *plan = NULL. Single-release: a second
// call with *plan == NULL is a documented no-op (never double-frees).
void pty_plan_free(pty_exec_plan** plan);

// ── L1-shim(b): spawn ────────────────────────────────────────────────────────────────────
typedef struct {
    pid_t pid;
    int   master_fd;
    int   err_no;
    int   failed_step; // 0=none, 1=fork, 2=prctl, 3=parent_died, 4=chdir, 5=exec, 6=handshake_timeout, 7=cancelled
    char  start_identity[128]; // "mac:{bootsessionuuid}:{p_uniqueid}" / "lx:{boot_id}:{starttime}"; "" = uncapturable
} pty_spawn_result;

enum {
    PTY_STEP_NONE = 0,
    PTY_STEP_FORK,
    PTY_STEP_PRCTL,
    PTY_STEP_PARENT_DIED,
    PTY_STEP_CHDIR,
    PTY_STEP_EXEC,
    PTY_STEP_HANDSHAKE_TIMEOUT,
    PTY_STEP_CANCELLED
};

// forkpty + child sequence + error-pipe handshake. 0 on success (out populated, out->pid > 0,
// out->master_fd valid), -1 on failure (out->err_no/out->failed_step set, no live unobserved
// child left behind — see pty_shim.c for the full contract). start_identity is captured
// IN THE PARENT, immediately after forkpty returns, before the child can be reaped by
// anything (the capture-binding rule) — an empty string means uncapturable, NOT a failure.
int pty_spawn(const pty_exec_plan *plan, char *const envp[], const char *cwd,
              unsigned short rows, unsigned short cols,
              pid_t expected_parent, int cancel_fd, pty_spawn_result *out);

#ifdef __APPLE__
// Captures `mac:{kern.bootsessionuuid}:{p_uniqueid}` for `pid` into `out` (>= 128 bytes),
// NUL-terminated. Returns 1 on success, 0 if the private-ABI call is unavailable/anomalous
// (short read, EINVAL, zero id) — spare-shaped, never a false proof. Exported so it can be
// called BOTH from pty_spawn's internal post-forkpty capture and (via a separate P/Invoke)
// from the managed ProcessStartToken.ForPid comparison path for an arbitrary already-running
// pid — see this task's design note for why there are still two independent call sites.
int pty_capture_mac_identity(pid_t pid, char *out, size_t outlen);
#endif

#ifdef __cplusplus
}
#endif

#endif // PTY_SHIM_H

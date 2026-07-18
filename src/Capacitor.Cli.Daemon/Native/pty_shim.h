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

// pty_spawn_result / pty_spawn are declared in Task 3's addition to this header.

#ifdef __cplusplus
}
#endif

#endif // PTY_SHIM_H

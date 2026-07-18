#include "pty_shim.h"

#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/xattr.h>
#include <sys/syscall.h>
#include <fcntl.h>
#include <unistd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>

// ── existing (unchanged) ────────────────────────────────────────────────────────────────
int pty_set_winsize(int fd, unsigned short rows, unsigned short cols) {
    struct winsize ws = {0};
    ws.ws_row = rows;
    ws.ws_col = cols;
    return ioctl(fd, TIOCSWINSZ, &ws);
}

// ── L1-shim(a): execution-plan construction ─────────────────────────────────────────────
//
// The fd-bound kernel-floor probe (execveat/AT_EMPTY_PATH) and the fd-bound privilege
// preflight (fgetxattr(security.capability)) are genuinely Linux-only kernel/glibc
// features; every use of them below is `#ifdef __linux__`-guarded so this file still
// compiles (and the portable PATH/shebang plan-construction logic still runs) on other
// POSIX platforms such as macOS — but on those platforms containment can never be proven,
// so every plan degrades to uncontained EXEC_PATH/EXEC_FD-uncontained, matching the
// fail-closed rule ("never a false proof").

#define PTY_EXEC_FD   1
#define PTY_EXEC_PATH 2

struct pty_exec_plan {
    int    mode;       // PTY_EXEC_FD or PTY_EXEC_PATH
    int    exec_fd;    // valid iff mode == PTY_EXEC_FD; -1 otherwise. O_CLOEXEC — closes on
                        // successful exec automatically; freed explicitly on every other path.
    char  *exec_path;  // valid iff mode == PTY_EXEC_PATH; NULL otherwise. Owned (strdup'd).
    char **argv;       // NULL-terminated, owned (deep-copied).
    int    contained;  // 1 = EXEC_FD + proven non-privileged; 0 = uncontained.
};

int pty_probe_execveat(void) {
#ifdef __linux__
    errno = 0;
    // Deliberately bogus fd (-1) + empty pathname via the RAW syscall — bypasses any glibc
    // wrapper (only added in glibc 2.34) so the floor stays a KERNEL floor. EBADF means the
    // syscall validated flags and reached fd validation (i.e. it EXISTS); ENOSYS means it
    // doesn't. Every OTHER errno (EINVAL, EPERM from a seccomp filter, anything else) is
    // fail-safe treated as unsupported — classification must never be left undefined.
    long rc = syscall(SYS_execveat, -1, "", NULL, NULL, AT_EMPTY_PATH);
    if (rc == 0) return 1;
    return errno == EBADF ? 1 : 0;
#else
    // execveat is Linux-only; every other platform takes the EXEC_PATH-uncontained fallback.
    return 0;
#endif
}

static char **dup_argv(char *const argv[]) {
    int n = 0;
    while (argv[n]) n++;
    char **copy = calloc((size_t)n + 1, sizeof(char*));
    if (!copy) return NULL;
    for (int i = 0; i < n; i++) {
        copy[i] = strdup(argv[i]);
        if (!copy[i]) { for (int j = 0; j < i; j++) free(copy[j]); free(copy); return NULL; }
    }
    copy[n] = NULL;
    return copy;
}

static void free_argv(char **argv) {
    if (!argv) return;
    for (int i = 0; argv[i]; i++) free(argv[i]);
    free(argv);
}

static int build_execpath_plan(const char *exe_abs_path, char *const orig_argv[], int contained, pty_exec_plan **out_plan) {
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    if (!plan) return -1;
    plan->mode      = PTY_EXEC_PATH;
    plan->exec_fd   = -1;
    plan->exec_path = strdup(exe_abs_path);
    plan->argv      = dup_argv(orig_argv);
    plan->contained = contained;
    if (!plan->exec_path || !plan->argv) { pty_exec_plan *p = plan; pty_plan_free(&p); return -1; }
    *out_plan = plan;
    return 0;
}

// fd-bound privilege preflight (pinned, spec §4.2): the check and the exec share ONE open
// file — never a path-based stat-then-execve (TOCTOU-broken). Only a clean "no bits, no
// capability xattr" proves non-privileged; any other outcome (including a read/stat ERROR)
// classifies uncontained — never a false proof.
static int fd_is_non_privileged(int fd) {
    struct stat st;
    if (fstat(fd, &st) != 0) return 0;
    if (st.st_mode & (S_ISUID | S_ISGID)) return 0;

#ifdef __linux__
    char buf[1];
    ssize_t r = fgetxattr(fd, "security.capability", buf, sizeof(buf));
    if (r >= 0) return 0; // has SOME capability xattr payload → privileged, uncontained
    // ENODATA (no xattr) or ENOTSUP (fs can't carry xattrs) are the ONLY proofs of absence.
    return (errno == ENODATA || errno == ENOTSUP) ? 1 : 0;
#else
    // No fd-bound capability-xattr proof exists off Linux; never claim proof of
    // non-privilege on a platform that can't back it up (fail-closed to uncontained).
    return 0;
#endif
}

static int build_execfd_plan(int fd, char *const argv[], int contained, pty_exec_plan **out_plan) {
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    if (!plan) { close(fd); return -1; }
    plan->mode      = PTY_EXEC_FD;
    plan->exec_fd   = fd;
    plan->exec_path = NULL;
    plan->argv      = dup_argv(argv);
    plan->contained = contained;
    if (!plan->argv) { pty_exec_plan *p = plan; pty_plan_free(&p); return -1; }
    *out_plan = plan;
    return 0;
}

// Resolves `name` against `path_env` (colon-separated). Returns a malloc'd absolute path or
// NULL if not found. Sets *saw_relative_component whenever ANY field is empty (a leading/
// trailing ':' or an internal '::' — POSIX treats an empty field as the current directory) or
// relative (does not begin with '/'), so the caller can classify uncontained rather than risk
// resolving against the wrong cwd (spec §4.2 risk #4).
//
// Deliberately a MANUAL ':' scan, NOT strtok: strtok collapses adjacent/leading/trailing
// delimiters, so it silently HIDES exactly the empty (cwd) field we must detect — and it is
// non-reentrant (mutates a hidden static), unsafe on the daemon threadpool threads that call
// this library. The scan detects relative components across the WHOLE PATH even after an
// absolute match, since the kernel would still try the cwd component at runtime.
static char *resolve_in_absolute_path(const char *name, const char *path_env, int *saw_relative_component) {
    *saw_relative_component = 0;
    if (!path_env) return NULL;

    size_t name_len = strlen(name);
    char *result = NULL;
    const char *p = path_env;
    for (;;) {
        const char *colon = strchr(p, ':');
        size_t len = colon ? (size_t)(colon - p) : strlen(p);

        if (len == 0 || p[0] != '/') {
            // Empty field (cwd) or a relative dir → cwd-dependent resolution → uncontained.
            *saw_relative_component = 1;
        } else if (!result) {
            size_t need = len + 1 + name_len + 1;
            char *candidate = malloc(need);
            if (candidate) {
                memcpy(candidate, p, len);
                candidate[len] = '/';
                memcpy(candidate + len + 1, name, name_len + 1); // includes the NUL
                struct stat st;
                if (access(candidate, X_OK) == 0 && stat(candidate, &st) == 0 && S_ISREG(st.st_mode)) {
                    result = candidate;
                } else {
                    free(candidate);
                }
            }
        }

        if (!colon) break;
        p = colon + 1;
    }
    return result;
}

static const char *find_env(char *const envp[], const char *key) {
    size_t klen = strlen(key);
    for (int i = 0; envp && envp[i]; i++) {
        if (strncmp(envp[i], key, klen) == 0 && envp[i][klen] == '=') return envp[i] + klen + 1;
    }
    return NULL;
}

// Builds the plan for a `#!interp [optarg]` (direct) or `#!/usr/bin/env NAME [...]` shebang.
// `head`/`head_len` is the first-256-bytes sniff already read from the ORIGINAL file (script)
// by the caller; the script fd itself is never exec'd or kept (TOCTOU rule — see spec).
static int build_shebang_plan(
        const char *script_abs_path, const char *head, ssize_t head_len,
        char *const orig_argv[], char *const envp[], int execveat_supported, pty_exec_plan **out_plan) {
    // Parse the shebang line: "#!<rest>\n"
    const char *line_end = memchr(head, '\n', (size_t)head_len);
    size_t line_len = line_end ? (size_t)(line_end - head) : (size_t)head_len;
    char line[256] = {0};
    memcpy(line, head + 2, line_len > 2 ? line_len - 2 : 0);

    // Split into up to 2 tokens (interp path, one optional arg-blob).
    char *save = NULL;
    char *tok0 = strtok_r(line, " \t", &save);
    char *rest = save; // everything after the first token, NOT re-tokenized yet

    if (!tok0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); // malformed → uncontained

    int is_env = strcmp(tok0, "/usr/bin/env") == 0 || strcmp(tok0, "env") == 0;

    if (!is_env) {
        // Direct shebang: at most ONE optional arg is kept as-is; anything with more tokens
        // after that is "an unresolvable shebang" per spec → uncontained.
        char *tok1 = rest ? strtok_r(NULL, " \t", &save) : NULL;
        char *tok2 = rest ? strtok_r(NULL, " \t", &save) : NULL;
        if (tok2) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        int fd = open(tok0, O_RDONLY | O_CLOEXEC);
        if (fd < 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        // Rewritten argv: [interp, optarg?, script_abs_path, orig_argv[1:]...]. Guard n == 0
        // (empty orig_argv) so the (n - 1) arithmetic can't under-size the array by a slot.
        int n = 0; while (orig_argv[n]) n++;
        if (n < 1) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }

        // Assemble a BORROWED (non-owning) NULL-terminated view, then let build_execfd_plan
        // deep-copy it with per-element NULL checks — no unchecked strdup/calloc on this path,
        // and any allocation failure funnels through to the uncontained EXEC_PATH fallback.
        int extra = tok1 ? 3 : 2; // interp (+ optarg) + script
        const char **view = calloc((size_t)(extra + (n - 1)) + 1, sizeof(char*));
        if (!view) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }
        int k = 0;
        view[k++] = tok0;
        if (tok1) view[k++] = tok1;
        view[k++] = script_abs_path;
        for (int i = 1; i < n; i++) view[k++] = orig_argv[i];
        view[k] = NULL;

        int contained = execveat_supported ? fd_is_non_privileged(fd) : 0;
        int rc = build_execfd_plan(fd, (char *const *)view, contained, out_plan);
        free(view);
        // build_execfd_plan owns fd on success AND failure (it closes fd on any error), so a
        // failed contained plan degrades to the uncontained EXEC_PATH plan (fd already closed).
        if (rc != 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
        return 0;
    }

    // `env [-S ...|VAR=val ...] NAME [args...]` — only the bare "env NAME" form (exactly one
    // token after `env`, no flags/assignments) is rewritten; anything richer → uncontained.
    char *name = rest ? strtok_r(NULL, " \t", &save) : NULL;
    char *extra_tok = name ? strtok_r(NULL, " \t", &save) : NULL;
    if (!name || extra_tok || name[0] == '-' || strchr(name, '=')) {
        return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    }

    const char *child_path = find_env(envp, "PATH");
    int saw_relative = 0;
    char *resolved = NULL;
    if (child_path) {
        resolved = resolve_in_absolute_path(name, child_path, &saw_relative);
    } else {
        // Unset child PATH → confstr(_CS_PATH) verbatim, at runtime, preserving its order.
        // A 0 return (unsupported/failed) or a malloc failure → can't resolve → uncontained.
        size_t need = confstr(_CS_PATH, NULL, 0);
        if (need == 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
        char *cs = malloc(need);
        if (!cs) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
        confstr(_CS_PATH, cs, need);
        resolved = resolve_in_absolute_path(name, cs, &saw_relative);
        free(cs);
    }

    if (saw_relative || !resolved) {
        // Empty/relative PATH component, or NAME simply not found in an absolute-only PATH →
        // uncontained either way (the kernel/env resolves it correctly at runtime; we just
        // forgo containment rather than risk preflighting the wrong inode). `resolved` can be
        // non-NULL here when an absolute component matched but an empty/relative SIBLING tripped
        // saw_relative — free it (free(NULL) is a no-op for the not-found case).
        free(resolved);
        return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    }

    int fd = open(resolved, O_RDONLY | O_CLOEXEC);
    free(resolved);
    if (fd < 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

    // Guard n == 0 (empty orig_argv) before the [name, script, orig_argv[1:]...] layout.
    int n = 0; while (orig_argv[n]) n++;
    if (n < 1) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }

    // Borrowed NULL-terminated view. argv[0] is the resolved interpreter name (env's target IS
    // the interpreter); the fd is what actually execs. build_execfd_plan deep-copies it with
    // per-element NULL checks — no unchecked strdup/calloc, allocation failure → uncontained.
    const char **view = calloc((size_t)(n + 2), sizeof(char*)); // [name, script, orig_argv[1:]..., NULL]
    if (!view) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }
    int k = 0;
    view[k++] = name;
    view[k++] = script_abs_path;
    for (int i = 1; i < n; i++) view[k++] = orig_argv[i];
    view[k] = NULL;

    int contained = execveat_supported ? fd_is_non_privileged(fd) : 0;
    int rc = build_execfd_plan(fd, (char *const *)view, contained, out_plan);
    free(view);
    if (rc != 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    return 0;
}

int pty_preflight(const char *exe_abs_path, char *const orig_argv[], char *const envp[],
                   int execveat_supported, pty_exec_plan **out_plan) {
    *out_plan = NULL;

    if (!execveat_supported) {
        return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan);
    }

    int fd = open(exe_abs_path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        // The plan can't be constructed AT ALL (not even the EXEC_PATH fallback needs an open
        // fd, but a nonexistent path fails EXEC_PATH too — execve would ENOENT identically) →
        // -1, the one case that is a genuine preflight failure.
        return -1;
    }

    char head[256];
    ssize_t n = read(fd, head, sizeof(head) - 1);
    if (n < 0) {
        close(fd);
        return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan); // degrade, don't fail
    }
    head[n] = '\0';

    if (n >= 2 && head[0] == '#' && head[1] == '!') {
        close(fd); // the SCRIPT fd is never exec'd — see the TOCTOU note in the header comment
        return build_shebang_plan(exe_abs_path, head, n, orig_argv, envp, execveat_supported, out_plan);
    }

    int contained = fd_is_non_privileged(fd);
    return build_execfd_plan(fd, orig_argv, contained, out_plan);
}

int pty_plan_contained(const pty_exec_plan *plan) {
    return plan ? plan->contained : 0;
}

void pty_plan_free(pty_exec_plan **plan) {
    if (!plan || !*plan) return;
    pty_exec_plan *p = *plan;
    if (p->mode == PTY_EXEC_FD && p->exec_fd >= 0) close(p->exec_fd);
    free(p->exec_path);
    free_argv(p->argv);
    free(p);
    *plan = NULL;
}

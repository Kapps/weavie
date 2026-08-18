// LD_PRELOAD lab for WebKitGTK's 60Hz cap: traces the pacing primitives (DRM vblank waits, the hardcoded
// 60fps timer's 16ms sleeps, EGL swap cadence) and can replace either pacer with a precise VBLANK_SHIM_HZ
// grid. Built and driven by tools/refresh-lab.sh; only activates in processes named in VBLANK_SHIM_COMM.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

// Mirrors libdrm's drmVBlank union (UAPI-stable), so building needs no libdrm headers.
typedef struct { unsigned type; unsigned sequence; unsigned long signal; } DrmVBlankReq;
typedef struct { unsigned type; unsigned sequence; long tval_sec; long tval_usec; } DrmVBlankRep;

static int enabled, fix_drm, force_drm, fix_timer, swap0;
static double hz = 240.0;
static _Atomic unsigned drm_calls, drm_fails, timer_hits, swap_calls;
static _Atomic int swap0_done;
static _Atomic uint64_t grid_next, swap_last;

static uint64_t now_ns(void) {
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return (uint64_t)t.tv_sec * 1000000000ull + (uint64_t)t.tv_nsec;
}

static void note(const char *fmt, ...) {
	va_list ap;
	va_start(ap, fmt);
	flockfile(stderr);
	fprintf(stderr, "[vblank-shim %d] ", (int)getpid());
	vfprintf(stderr, fmt, ap);
	fputc('\n', stderr);
	funlockfile(stderr);
	va_end(ap);
}

__attribute__((constructor)) static void shim_init(void) {
	char comm[64] = "";
	FILE *f = fopen("/proc/self/comm", "r");
	if (f) {
		if (!fgets(comm, sizeof comm, f))
			comm[0] = 0;
		fclose(f);
	}
	comm[strcspn(comm, "\n")] = 0;
	const char *only = getenv("VBLANK_SHIM_COMM");
	if (!only)
		only = "webkit-fps,WebKitWebProc,Weavie";
	char list[256];
	snprintf(list, sizeof list, "%s", only);
	for (char *tok = strtok(list, ","); tok; tok = strtok(NULL, ","))
		if (!strncmp(comm, tok, strlen(tok))) {
			enabled = 1;
			break;
		}
	if (!enabled)
		return;
	const char *e;
	fix_drm = (e = getenv("VBLANK_SHIM_FIX")) && *e && strcmp(e, "0");
	force_drm = (e = getenv("VBLANK_SHIM_FORCE")) && *e && strcmp(e, "0");
	fix_timer = (e = getenv("VBLANK_SHIM_TIMERFIX")) && *e && strcmp(e, "0");
	swap0 = (e = getenv("VBLANK_SHIM_SWAP0")) && *e && strcmp(e, "0");
	if ((e = getenv("VBLANK_SHIM_HZ")) && atof(e) > 0)
		hz = atof(e);
	note("active in '%s' (fix_drm=%d force_drm=%d fix_timer=%d swap0=%d hz=%.3f)",
		comm, fix_drm, force_drm, fix_timer, swap0, hz);
}

__attribute__((destructor)) static void shim_summary(void) {
	if (enabled && (drm_calls || timer_hits || swap_calls))
		note("summary: drmWaitVBlank=%u (failed %u), 16ms timer sleeps=%u, eglSwapBuffers=%u",
			(unsigned)drm_calls, (unsigned)drm_fails, (unsigned)timer_hits, (unsigned)swap_calls);
}

static int real_clock_nanosleep(clockid_t c, int flags, const struct timespec *rq, struct timespec *rm) {
	static int (*real)(clockid_t, int, const struct timespec *, struct timespec *);
	if (!real)
		real = dlsym(RTLD_NEXT, "clock_nanosleep");
	return real(c, flags, rq, rm);
}

// One shared pacing grid: every emulated wait sleeps to the next multiple of the period on the monotonic
// clock, so replacement vblanks neither drift nor double-fire.
static void sleep_to_grid(void) {
	uint64_t period = (uint64_t)(1000000000.0 / hz);
	uint64_t t = now_ns();
	uint64_t expected = atomic_load(&grid_next);
	uint64_t target = expected > t ? expected : ((t / period) + 1) * period;
	atomic_store(&grid_next, target + period);
	struct timespec ts = { (time_t)(target / 1000000000ull), (long)(target % 1000000000ull) };
	while (real_clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &ts, NULL) == EINTR) {
	}
}

int drmWaitVBlank(int fd, void *vbl) {
	static int (*real)(int, void *);
	if (!real)
		real = dlsym(RTLD_NEXT, "drmWaitVBlank");
	if (!enabled)
		return real ? real(fd, vbl) : (errno = ENOSYS, -1);
	DrmVBlankReq req = *(DrmVBlankReq *)vbl;
	unsigned n = atomic_fetch_add(&drm_calls, 1);
	int r = -1;
	int err = ENOSYS;
	if (!force_drm && real) {
		r = real(fd, vbl);
		err = errno;
	}
	if (r == 0) {
		if (n < 8 || n % 600 == 0)
			note("drmWaitVBlank #%u ok (fd=%d type=0x%x)", n, fd, req.type);
		return 0;
	}
	atomic_fetch_add(&drm_fails, 1);
	if (n < 8)
		note("drmWaitVBlank #%u FAILED fd=%d type=0x%x errno=%d (%s)%s",
			n, fd, req.type, err, strerror(err), (fix_drm || force_drm) ? " -> emulating at hz" : "");
	if (!fix_drm && !force_drm) {
		errno = err;
		return r;
	}
	sleep_to_grid();
	static _Atomic unsigned seq;
	uint64_t t = now_ns();
	DrmVBlankRep *rep = vbl;
	rep->type = req.type;
	rep->sequence = atomic_fetch_add(&seq, 1) + 1;
	rep->tval_sec = (long)(t / 1000000000ull);
	rep->tval_usec = (long)((t % 1000000000ull) / 1000);
	return 0;
}

// WebKit's fallback timer sleeps exactly milliseconds(1000 / 60) = 16ms per frame — a distinctive
// signature nothing else in these processes produces at frame cadence.
static int is_timer_sleep(const struct timespec *rq) {
	return rq && rq->tv_sec == 0 && rq->tv_nsec == 16000000;
}

static int timer_hit(const char *via) {
	unsigned n = atomic_fetch_add(&timer_hits, 1);
	if (n < 4 || n % 600 == 0)
		note("16ms timer sleep #%u via %s%s", n, via, fix_timer ? " -> paced to hz" : "");
	if (!fix_timer)
		return 0;
	sleep_to_grid();
	return 1;
}

int nanosleep(const struct timespec *rq, struct timespec *rm) {
	static int (*real)(const struct timespec *, struct timespec *);
	if (!real)
		real = dlsym(RTLD_NEXT, "nanosleep");
	if (enabled && is_timer_sleep(rq) && timer_hit("nanosleep"))
		return 0;
	return real(rq, rm);
}

int clock_nanosleep(clockid_t c, int flags, const struct timespec *rq, struct timespec *rm) {
	if (enabled && flags == 0 && is_timer_sleep(rq) && timer_hit("clock_nanosleep"))
		return 0;
	return real_clock_nanosleep(c, flags, rq, rm);
}

unsigned int eglSwapBuffers(void *display, void *surface) {
	static unsigned int (*real)(void *, void *);
	static unsigned int (*real_interval)(void *, int);
	if (!real)
		real = dlsym(RTLD_NEXT, "eglSwapBuffers");
	if (enabled) {
		if (swap0 && !atomic_exchange(&swap0_done, 1)) {
			if (!real_interval)
				real_interval = dlsym(RTLD_NEXT, "eglSwapInterval");
			if (real_interval)
				note("forced eglSwapInterval(0) -> %u", real_interval(display, 0));
		}
		unsigned n = atomic_fetch_add(&swap_calls, 1);
		uint64_t t = now_ns();
		uint64_t prev = atomic_exchange(&swap_last, t);
		if (prev && (n < 6 || n % 240 == 0))
			note("eglSwapBuffers #%u dt=%.2fms", n, (double)(t - prev) / 1e6);
	}
	return real(display, surface);
}

unsigned int eglSwapInterval(void *display, int interval) {
	static unsigned int (*real)(void *, int);
	if (!real)
		real = dlsym(RTLD_NEXT, "eglSwapInterval");
	if (enabled)
		note("app eglSwapInterval(%d)%s", interval, swap0 ? " -> 0" : "");
	return real(display, swap0 ? 0 : interval);
}

int gdk_monitor_get_refresh_rate(void *monitor) {
	static int (*real)(void *);
	static _Atomic int last = -1;
	if (!real)
		real = dlsym(RTLD_NEXT, "gdk_monitor_get_refresh_rate");
	int rate = real(monitor);
	if (enabled && atomic_exchange(&last, rate) != rate)
		note("gdk_monitor_get_refresh_rate -> %d mHz", rate);
	return rate;
}

// LD_PRELOAD lab for WebKitGTK's 60Hz cap. Traces the pacing primitives (DRM vblank waits, the hardcoded
// 60fps timer's 16ms sleeps, EGL swap cadence) and the whole DRM discovery path WebKit's vblank monitor
// walks (drmGetDevices2 -> resources -> connector -> encoder -> crtc). Two repair modes: VBLANK_SHIM_FIX
// emulates drmWaitVBlank on a precise VBLANK_SHIM_HZ grid when the driver refuses it, and
// VBLANK_SHIM_STEER fills in the encoder/crtc ids some drivers hide from non-master clients so the
// monitor constructs at all (a wrong crtc only fails the wait, which the emulator covers). Built and
// driven by tools/refresh-lab.sh; only activates in processes named in VBLANK_SHIM_COMM.
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

static int enabled, fix_drm, force_drm, fix_timer, swap0, steer;
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
	steer = (e = getenv("VBLANK_SHIM_STEER")) && *e && strcmp(e, "0");
	if ((e = getenv("VBLANK_SHIM_HZ")) && atof(e) > 0)
		hz = atof(e);
	note("active in '%s' (fix_drm=%d force_drm=%d fix_timer=%d swap0=%d steer=%d hz=%.3f)",
		comm, fix_drm, force_drm, fix_timer, swap0, steer, hz);
}

__attribute__((destructor)) static void shim_summary(void) {
	if (enabled && (drm_calls || timer_hits || swap_calls))
		note("summary: drmWaitVBlank=%u (failed %u), 16ms timer sleeps=%u, eglSwapBuffers=%u",
			(unsigned)drm_calls, (unsigned)drm_fails, (unsigned)timer_hits, (unsigned)swap_calls);
}


// RTLD_NEXT can miss symbols a caller satisfies internally (Mesa's EGL carries its own libdrm copy and
// still dispatches through the interposable PLT), so resolution falls back to the canonical library —
// and a wrapper must never forward to NULL.
static void *resolve(const char *sym, const char *lib) {
	void *p = dlsym(RTLD_NEXT, sym);
	if (p == NULL && lib != NULL) {
		void *handle = dlopen(lib, RTLD_LAZY | RTLD_LOCAL);
		if (handle != NULL)
			p = dlsym(handle, sym);
	}
	return p;
}

static int real_clock_nanosleep(clockid_t c, int flags, const struct timespec *rq, struct timespec *rm) {
	static int (*real)(clockid_t, int, const struct timespec *, struct timespec *);
	if (!real)
		real = resolve("clock_nanosleep", NULL);
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


// ---- DRM discovery: every call DisplayVBlankMonitorDRM::create() can make, so a trace shows exactly
// where it gives up. Struct mirrors cover only the UAPI-stable prefixes the logging reads.

typedef struct { char **nodes; int available_nodes; int bustype; } DrmDeviceHead;

typedef struct {
	int count_fbs; uint32_t *fbs;
	int count_crtcs; uint32_t *crtcs;
	int count_connectors; uint32_t *connectors;
	int count_encoders; uint32_t *encoders;
	uint32_t min_width, max_width, min_height, max_height;
} DrmModeRes;

typedef struct {
	uint32_t connector_id, encoder_id, connector_type, connector_type_id;
	int connection;
	uint32_t mm_width, mm_height;
	int subpixel;
	int count_modes; void *modes;
	int count_props; uint32_t *props; uint64_t *prop_values;
	int count_encoders; uint32_t *encoders;
} DrmModeConnector;

typedef struct { uint32_t encoder_id, encoder_type, crtc_id, possible_crtcs, possible_clones; } DrmModeEncoder;

static _Atomic uint32_t steer_crtc;
static _Atomic int gdk_w_mm, gdk_h_mm;

static void fd_path(int fd, char *buf, size_t n) {
	char link[64];
	snprintf(link, sizeof link, "/proc/self/fd/%d", fd);
	ssize_t r = readlink(link, buf, n - 1);
	buf[r > 0 ? r : 0] = 0;
}

int drmGetDevices2(uint32_t flags, void **devices, int max_devices) {
	static int (*real)(uint32_t, void **, int);
	if (!real)
		real = resolve("drmGetDevices2", "libdrm.so.2");
	if (real == NULL)
		return -ENOSYS;
	int r = real(flags, devices, max_devices);
	if (enabled) {
		note("drmGetDevices2(max=%d) -> %d", max_devices, r);
		if (devices)
			for (int i = 0; i < r; i++) {
				DrmDeviceHead *d = devices[i];
				note("  device[%d] available_nodes=0x%x primary=%s", i, d->available_nodes,
					(d->available_nodes & 1) && d->nodes ? d->nodes[0] : "(none)");
			}
	}
	return r;
}

void *drmModeGetResources(int fd) {
	static void *(*real)(int);
	if (!real)
		real = resolve("drmModeGetResources", "libdrm.so.2");
	if (real == NULL)
		return NULL;
	DrmModeRes *res = real(fd);
	if (enabled) {
		char path[128];
		fd_path(fd, path, sizeof path);
		if (res == NULL) {
			note("drmModeGetResources(fd=%d %s) -> NULL errno=%d", fd, path, errno);
		} else {
			if (res->count_crtcs > 0)
				atomic_store(&steer_crtc, res->crtcs[0]);
			note("drmModeGetResources(fd=%d %s) -> crtcs=%d connectors=%d encoders=%d",
				fd, path, res->count_crtcs, res->count_connectors, res->count_encoders);
		}
	}
	return res;
}

void *drmModeGetConnector(int fd, uint32_t connector_id) {
	static void *(*real)(int, uint32_t);
	if (!real)
		real = resolve("drmModeGetConnector", "libdrm.so.2");
	if (real == NULL)
		return NULL;
	DrmModeConnector *c = real(fd, connector_id);
	if (enabled) {
		if (c == NULL) {
			note("drmModeGetConnector(fd=%d id=%u) -> NULL errno=%d", fd, connector_id, errno);
		} else {
			note("drmModeGetConnector(fd=%d id=%u) connection=%d encoder_id=%u mm=%ux%u encoders=%d",
				fd, connector_id, c->connection, c->encoder_id, c->mm_width, c->mm_height, c->count_encoders);
			if (steer && c->connection == 1) {
				int gw = atomic_load(&gdk_w_mm);
				int gh = atomic_load(&gdk_h_mm);
				int dw = (int)c->mm_width - gw;
				int dh = (int)c->mm_height - gh;
				// cm-quantisation error is bounded by +-5mm per axis; anything further apart is a different monitor.
				if (gw > 0 && gh > 0 && (dw != 0 || dh != 0) && dw >= -10 && dw <= 10 && dh >= -10 && dh <= 10) {
					note("  steer: kernel mm %ux%u (EDID cm-fields x10) != gdk %dx%d (compositor's detailed-timing mm)"
						" -> reporting gdk's so WebKit's exact match succeeds", c->mm_width, c->mm_height, gw, gh);
					c->mm_width = (uint32_t)gw;
					c->mm_height = (uint32_t)gh;
				}
			}
			if (steer && c->connection == 1 && c->encoder_id == 0 && c->count_encoders > 0 && c->encoders) {
				c->encoder_id = c->encoders[0];
				note("  steer: connected but encoder_id=0 -> using encoder %u", c->encoder_id);
			}
		}
	}
	return c;
}

void *drmModeGetEncoder(int fd, uint32_t encoder_id) {
	static void *(*real)(int, uint32_t);
	if (!real)
		real = resolve("drmModeGetEncoder", "libdrm.so.2");
	if (real == NULL)
		return NULL;
	DrmModeEncoder *enc = real(fd, encoder_id);
	if (enabled) {
		if (enc == NULL) {
			note("drmModeGetEncoder(fd=%d id=%u) -> NULL errno=%d", fd, encoder_id, errno);
		} else {
			note("drmModeGetEncoder(fd=%d id=%u) crtc_id=%u", fd, encoder_id, enc->crtc_id);
			uint32_t fallback = atomic_load(&steer_crtc);
			if (steer && enc->crtc_id == 0 && fallback != 0) {
				enc->crtc_id = fallback;
				note("  steer: crtc_id=0 -> using crtc %u (a wrong pipe only fails the wait, which the emulator covers)", fallback);
			}
		}
	}
	return enc;
}

int gdk_monitor_get_width_mm(void *monitor) {
	static int (*real)(void *);
	static _Atomic unsigned seen;
	if (!real)
		real = resolve("gdk_monitor_get_width_mm", "libgdk-3.so.0");
	if (real == NULL)
		return 0;
	int mm = real(monitor);
	atomic_store(&gdk_w_mm, mm);
	if (enabled && atomic_fetch_add(&seen, 1) < 4)
		note("gdk_monitor_get_width_mm -> %d (screen lookup succeeded; discovery is running)", mm);
	return mm;
}

int gdk_monitor_get_height_mm(void *monitor) {
	static int (*real)(void *);
	if (!real)
		real = resolve("gdk_monitor_get_height_mm", "libgdk-3.so.0");
	if (real == NULL)
		return 0;
	int mm = real(monitor);
	atomic_store(&gdk_h_mm, mm);
	return mm;
}

int drmWaitVBlank(int fd, void *vbl) {
	static int (*real)(int, void *);
	if (!real)
		real = resolve("drmWaitVBlank", "libdrm.so.2");
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
		real = resolve("nanosleep", NULL);
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
		real = resolve("eglSwapBuffers", "libEGL.so.1");
	if (real == NULL)
		return 0;
	if (enabled) {
		if (swap0 && !atomic_exchange(&swap0_done, 1)) {
			if (!real_interval)
				real_interval = resolve("eglSwapInterval", "libEGL.so.1");
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
		real = resolve("eglSwapInterval", "libEGL.so.1");
	if (real == NULL)
		return 0;
	if (enabled)
		note("app eglSwapInterval(%d)%s", interval, swap0 ? " -> 0" : "");
	return real(display, swap0 ? 0 : interval);
}

int gdk_monitor_get_refresh_rate(void *monitor) {
	static int (*real)(void *);
	static _Atomic int last = -1;
	if (!real)
		real = resolve("gdk_monitor_get_refresh_rate", "libgdk-3.so.0");
	if (real == NULL)
		return 0;
	int rate = real(monitor);
	if (enabled && atomic_exchange(&last, rate) != rate)
		note("gdk_monitor_get_refresh_rate -> %d mHz", rate);
	return rate;
}

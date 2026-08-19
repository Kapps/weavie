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
#include <fcntl.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

// Mirrors libdrm's drmVBlank union (UAPI-stable), so building needs no libdrm headers.
typedef struct { unsigned type; unsigned sequence; unsigned long signal; } DrmVBlankReq;
typedef struct { unsigned type; unsigned sequence; long tval_sec; long tval_usec; } DrmVBlankRep;

static int enabled, fix_drm, force_drm, fix_timer, swap0, steer, hz_from_env, fastfence, syncpatch;
static double hz = 240.0;
static _Atomic unsigned drm_calls, drm_fails, timer_hits, swap_calls;
static _Atomic unsigned commit_calls, frame_reqs, attach_calls, shm_buffers, dmabuf_buffers, draw_calls;
static _Atomic unsigned framecb_events, release_events, fence_dups, fence_signals;
static _Atomic unsigned flush_calls, clientwait_calls;
// Steady-state distributions for the three edges of the present loop: how often the surface commits, how
// fast the compositor answers a commit, and how long the client then sits before committing again.
static _Atomic unsigned hist_commit[6], hist_cb_latency[6], hist_commit_after_cb[6];
static _Atomic unsigned sync_injected, sync_dropped;
static void *(*real_marshal_flags)(void *, uint32_t, const void *, uint32_t, uint32_t, void *);
static void (*real_marshal_array_fn)(void *, uint32_t, void *);
static uint32_t (*real_proxy_version)(void *);
static int sync_observe(void *proxy, uint32_t opcode, void *args, const char *cls);
static void sync_observe_created(void *proxy, uint32_t opcode, void *args, void *created, const char *cls);
static _Atomic int fence_fd_ring[16];
static _Atomic uint64_t fence_birth_ring[16];
static _Atomic uint64_t commit_last, draw_last, framecb_last, release_last;
// The last frame-callback proxies handed out by wl_surface.frame, so listener wrapping can tell a real
// frame callback from a wl_display.sync roundtrip (both are wl_callback objects).
static _Atomic(void *) frame_proxies[64];
static _Atomic unsigned frame_proxy_next;

static void frame_proxy_add(void *proxy) {
	frame_proxies[atomic_fetch_add(&frame_proxy_next, 1) % 64] = proxy;
}

// Consumes the entry: a fired wl_callback is destroyed and malloc recycles its address, so a stale match
// would misclassify a later display.sync callback as a frame callback.
static int frame_proxy_known(void *proxy) {
	for (unsigned i = 0; i < 64; i++) {
		void *expected = proxy;
		if (atomic_compare_exchange_strong(&frame_proxies[i], &expected, (void *)NULL))
			return 1;
	}
	return 0;
}
static _Atomic int swap0_done;
static _Atomic uint64_t grid_next, swap_last;

static void note(const char *fmt, ...);

static void bucket(_Atomic unsigned *hist, uint64_t ns) {
	double ms = (double)ns / 1e6;
	int i = ms < 2 ? 0 : ms < 6 ? 1 : ms < 10 ? 2 : ms < 14 ? 3 : ms < 18 ? 4 : 5;
	atomic_fetch_add(&hist[i], 1);
}

static void hist_note(const char *name, _Atomic unsigned *hist) {
	note("hist %-18s <2ms:%-4u 2-6:%-4u 6-10:%-4u 10-14:%-4u 14-18:%-4u >=18:%u", name,
		(unsigned)hist[0], (unsigned)hist[1], (unsigned)hist[2],
		(unsigned)hist[3], (unsigned)hist[4], (unsigned)hist[5]);
}

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
	fastfence = (e = getenv("VBLANK_SHIM_FASTFENCE")) && *e && strcmp(e, "0");
	syncpatch = (e = getenv("VBLANK_SHIM_SYNCPATCH")) && *e && strcmp(e, "0");
	if ((e = getenv("VBLANK_SHIM_HZ")) && atof(e) > 0) {
		hz = atof(e);
		hz_from_env = 1;
	}
	note("active in '%s' (fix_drm=%d force_drm=%d fix_timer=%d swap0=%d steer=%d fastfence=%d syncpatch=%d hz=%.3f)",
		comm, fix_drm, force_drm, fix_timer, swap0, steer, fastfence, syncpatch, hz);
}

__attribute__((destructor)) static void shim_summary(void) {
	if (enabled && (drm_calls || timer_hits || swap_calls))
		note("summary: drmWaitVBlank=%u (failed %u), 16ms timer sleeps=%u, eglSwapBuffers=%u",
			(unsigned)drm_calls, (unsigned)drm_fails, (unsigned)timer_hits, (unsigned)swap_calls);
	if (enabled && (commit_calls || draw_calls || attach_calls))
		note("summary: wl commits=%u, frame reqs=%u, attaches=%u, shm buffers=%u, dmabuf buffers=%u, gl draws=%u",
			(unsigned)commit_calls, (unsigned)frame_reqs, (unsigned)attach_calls,
			(unsigned)shm_buffers, (unsigned)dmabuf_buffers, (unsigned)draw_calls);
	if (enabled && (framecb_events || release_events))
		note("summary: frame callbacks delivered=%u, buffer releases delivered=%u",
			(unsigned)framecb_events, (unsigned)release_events);
	if (enabled && commit_calls > 30) {
		hist_note("commit-interval", hist_commit);
		hist_note("cb-after-commit", hist_cb_latency);
		hist_note("commit-after-cb", hist_commit_after_cb);
	}
	if (enabled && (sync_injected || sync_dropped))
		note("summary: syncpatch armed %u naked commits, dropped %u SHM attaches",
			(unsigned)sync_injected, (unsigned)sync_dropped);
	if (enabled && (fence_dups || flush_calls || clientwait_calls))
		note("summary: sync_file exports=%u, observed signals=%u, glFlush=%u, eglClientWaitSync=%u",
			(unsigned)fence_dups, (unsigned)fence_signals,
			(unsigned)flush_calls, (unsigned)clientwait_calls);
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


// ---- presentation tracing: WebKit's GTK3 backing store paints with gdk_cairo_draw_from_gl during the
// widget's draw cycle and only then tells the web process FrameDone, so the paint cadence IS the frame
// pacer; the commit stream underneath shows what actually reaches the compositor and with which buffers.

void gdk_cairo_draw_from_gl(void *cr, void *window, int source, int source_type, int buffer_scale,
	int x, int y, int width, int height) {
	static void (*real)(void *, void *, int, int, int, int, int, int, int);
	if (!real)
		real = resolve("gdk_cairo_draw_from_gl", "libgdk-3.so.0");
	if (real == NULL)
		return;
	if (!enabled) {
		real(cr, window, source, source_type, buffer_scale, x, y, width, height);
		return;
	}
	unsigned n = atomic_fetch_add(&draw_calls, 1);
	uint64_t t0 = now_ns();
	real(cr, window, source, source_type, buffer_scale, x, y, width, height);
	uint64_t t1 = now_ns();
	uint64_t prev = atomic_exchange(&draw_last, t1);
	if (n < 12 || n % 240 == 0)
		note("gdk_cairo_draw_from_gl #%u dt=%.2fms took=%.2fms (%dx%d)", n,
			prev ? (double)(t1 - prev) / 1e6 : 0.0, (double)(t1 - t0) / 1e6, width, height);
}

static const char *wl_cls(void *proxy) {
	static const char *(*get_class)(void *);
	if (!get_class)
		get_class = resolve("wl_proxy_get_class", "libwayland-client.so.0");
	return get_class ? get_class(proxy) : NULL;
}

static int wl_track(void *proxy, uint32_t opcode, void *args) {
	if (!enabled)
		return 0;
	const char *cls = wl_cls(proxy);
	if (cls == NULL)
		return 0;
	if (sync_observe(proxy, opcode, args, cls))
		return 1;
	if (strcmp(cls, "wl_surface") == 0) {
		if (opcode == 6) {
			unsigned n = atomic_fetch_add(&commit_calls, 1);
			uint64_t t = now_ns();
			uint64_t prev = atomic_exchange(&commit_last, t);
			if (prev != 0)
				bucket(hist_commit, t - prev);
			uint64_t cb = atomic_load(&framecb_last);
			if (cb != 0 && t > cb)
				bucket(hist_commit_after_cb, t - cb);
			if (n < 12 || n % 240 == 0)
				note("wl_surface.commit #%u dt=%.2fms", n, prev ? (double)(t - prev) / 1e6 : 0.0);
		} else if (opcode == 3) {
			atomic_fetch_add(&frame_reqs, 1);
		} else if (opcode == 1) {
			atomic_fetch_add(&attach_calls, 1);
		}
		return 0;
	} else if (strcmp(cls, "wl_shm_pool") == 0 && opcode == 0) {
		atomic_fetch_add(&shm_buffers, 1);
	} else if (strcmp(cls, "zwp_linux_buffer_params_v1") == 0 && (opcode == 1 || opcode == 2)) {
		atomic_fetch_add(&dmabuf_buffers, 1);
	}
	return 0;
}

void wl_proxy_marshal_array(void *proxy, uint32_t opcode, void *args) {
	static void (*real)(void *, uint32_t, void *);
	if (!real) {
		real = resolve("wl_proxy_marshal_array", "libwayland-client.so.0");
		real_marshal_array_fn = real;
	}
	if (real == NULL)
		return;
	if (wl_track(proxy, opcode, args))
		return;
	real(proxy, opcode, args);
}

void *wl_proxy_marshal_array_constructor(void *proxy, uint32_t opcode, void *args, const void *interface) {
	static void *(*real)(void *, uint32_t, void *, const void *);
	if (!real)
		real = resolve("wl_proxy_marshal_array_constructor", "libwayland-client.so.0");
	if (real == NULL)
		return NULL;
	if (wl_track(proxy, opcode, args))
		return NULL;
	void *created = real(proxy, opcode, args, interface);
	if (enabled && opcode == 3 && created != NULL)
		frame_proxy_add(created);
	if (enabled && created != NULL)
		sync_observe_created(proxy, opcode, args, created, wl_cls(proxy));
	return created;
}

void *wl_proxy_marshal_array_constructor_versioned(void *proxy, uint32_t opcode, void *args,
	const void *interface, uint32_t version) {
	static void *(*real)(void *, uint32_t, void *, const void *, uint32_t);
	if (!real)
		real = resolve("wl_proxy_marshal_array_constructor_versioned", "libwayland-client.so.0");
	if (real == NULL)
		return NULL;
	if (wl_track(proxy, opcode, args))
		return NULL;
	void *created = real(proxy, opcode, args, interface, version);
	if (enabled && opcode == 3 && created != NULL)
		frame_proxy_add(created);
	if (enabled && created != NULL)
		sync_observe_created(proxy, opcode, args, created, wl_cls(proxy));
	return created;
}

void *wl_proxy_marshal_array_flags(void *proxy, uint32_t opcode, const void *interface, uint32_t version,
	uint32_t flags, void *args) {
	static void *(*real)(void *, uint32_t, const void *, uint32_t, uint32_t, void *);
	if (!real) {
		real = resolve("wl_proxy_marshal_array_flags", "libwayland-client.so.0");
		real_marshal_flags = real;
		real_proxy_version = (uint32_t (*)(void *))resolve("wl_proxy_get_version", "libwayland-client.so.0");
	}
	if (real == NULL)
		return NULL;
	if (wl_track(proxy, opcode, args))
		return NULL;
	void *created = real(proxy, opcode, interface, version, flags, args);
	if (enabled && opcode == 3 && created != NULL)
		frame_proxy_add(created);
	if (enabled && created != NULL)
		sync_observe_created(proxy, opcode, args, created, wl_cls(proxy));
	return created;
}


// ---- event delivery: frame callbacks and buffer releases are server->client, invisible to the marshal
// wrappers — wrapping the listeners at registration shows which of the two arrives at the clamped rate.

typedef struct { void **listener; void *data; } WlThunk;

static void frame_done_thunk(void *data, void *callback, uint32_t serial) {
	WlThunk *thunk = data;
	unsigned n = atomic_fetch_add(&framecb_events, 1);
	uint64_t t = now_ns();
	uint64_t prev = atomic_exchange(&framecb_last, t);
	uint64_t commit = atomic_load(&commit_last);
	if (commit != 0 && t > commit)
		bucket(hist_cb_latency, t - commit);
	if (n < 12 || n % 240 == 0)
		note("frame callback #%u dt=%.2fms latency-after-commit=%.2fms", n,
			prev ? (double)(t - prev) / 1e6 : 0.0, (double)(t - commit) / 1e6);
	((void (*)(void *, void *, uint32_t))thunk->listener[0])(thunk->data, callback, serial);
}

static void buffer_release_thunk(void *data, void *buffer) {
	WlThunk *thunk = data;
	unsigned n = atomic_fetch_add(&release_events, 1);
	uint64_t t = now_ns();
	uint64_t prev = atomic_exchange(&release_last, t);
	if (n < 12 || n % 240 == 0)
		note("wl_buffer release #%u dt=%.2fms latency-after-commit=%.2fms", n,
			prev ? (double)(t - prev) / 1e6 : 0.0, (double)(t - atomic_load(&commit_last)) / 1e6);
	((void (*)(void *, void *))thunk->listener[0])(thunk->data, buffer);
}

int wl_proxy_add_listener(void *proxy, void (**implementation)(void), void *data) {
	static int (*real)(void *, void (**)(void), void *);
	static const char *(*get_class)(void *);
	if (!real)
		real = resolve("wl_proxy_add_listener", "libwayland-client.so.0");
	if (real == NULL)
		return -1;
	if (!enabled)
		return real(proxy, implementation, data);
	if (!get_class)
		get_class = resolve("wl_proxy_get_class", "libwayland-client.so.0");
	const char *cls = get_class ? get_class(proxy) : NULL;
	if (cls != NULL && (strcmp(cls, "wl_callback") == 0 || strcmp(cls, "wl_buffer") == 0)) {
		int frame = strcmp(cls, "wl_callback") == 0;
		if (!frame || frame_proxy_known(proxy)) {
			// Lab tool: one small allocation per wrapped listener, never freed.
			WlThunk *thunk = malloc(sizeof *thunk);
			if (thunk != NULL) {
				thunk->listener = (void **)implementation;
				thunk->data = data;
				static void (*frame_thunk[1])(void);
				static void (*release_thunk[1])(void);
				frame_thunk[0] = (void (*)(void))frame_done_thunk;
				release_thunk[0] = (void (*)(void))buffer_release_thunk;
				return real(proxy, frame ? frame_thunk : release_thunk, thunk);
			}
		}
	}
	return real(proxy, implementation, data);
}


// ---- render fence: the UI process paints a dmabuf frame only after the web process's render fence
// signals, so the fence's creation-to-signal latency is the last unmeasured edge of the 60Hz loop.
// fastfence replaces it: glFinish (a short synchronous GPU wait) plus an already-readable pipe.

static void (*glfinish_from_getproc)(void);
static int (*dupfence_from_getproc)(void *, void *);

static void fence_ring_add(int fd) {
	uint64_t t = now_ns();
	for (unsigned i = 0; i < 16; i++) {
		int empty = -1;
		if (atomic_compare_exchange_strong(&fence_fd_ring[i], &empty, fd)) {
			fence_birth_ring[i] = t;
			return;
		}
	}
}

static void fence_check_signal(int fd) {
	for (unsigned i = 0; i < 16; i++) {
		if (atomic_load(&fence_fd_ring[i]) == fd) {
			unsigned n = atomic_fetch_add(&fence_signals, 1);
			if (n < 12 || n % 240 == 0)
				note("render fence fd=%d signaled after %.2fms", fd,
					(double)(now_ns() - fence_birth_ring[i]) / 1e6);
			atomic_store(&fence_fd_ring[i], -1);
			return;
		}
	}
}

__attribute__((constructor)) static void fence_ring_init(void) {
	for (unsigned i = 0; i < 16; i++)
		atomic_store(&fence_fd_ring[i], -1);
}

int eglDupNativeFenceFDANDROID(void *dpy, void *sync) {
	unsigned n = atomic_fetch_add(&fence_dups, 1);
	if (fastfence) {
		if (glfinish_from_getproc == NULL)
			glfinish_from_getproc = (void (*)(void))resolve("glFinish", "libGLESv2.so.2");
		if (glfinish_from_getproc == NULL)
			glfinish_from_getproc = (void (*)(void))resolve("glFinish", "libGL.so.1");
		int fds[2];
		if (glfinish_from_getproc != NULL && pipe2(fds, O_CLOEXEC) == 0) {
			uint64_t t0 = now_ns();
			glfinish_from_getproc();
			char byte = 1;
			if (write(fds[1], &byte, 1) < 0) { /* readable either way once closed */ }
			close(fds[1]);
			if (n < 8 || n % 600 == 0)
				note("fastfence #%u: glFinish took %.2fms -> pre-signaled pipe fd=%d",
					n, (double)(now_ns() - t0) / 1e6, fds[0]);
			return fds[0];
		}
	}
	int (*real)(void *, void *) = dupfence_from_getproc;
	if (real == NULL)
		real = (int (*)(void *, void *))resolve("eglDupNativeFenceFDANDROID", "libEGL.so.1");
	if (real == NULL)
		return -1;
	int fd = real(dpy, sync);
	if (fd >= 0) {
		fence_ring_add(fd);
		if (n < 8 || n % 600 == 0)
			note("render fence created fd=%d (#%u)", fd, n);
	}
	return fd;
}

int poll(struct pollfd *fds, nfds_t nfds, int timeout) {
	static int (*real)(struct pollfd *, nfds_t, int);
	if (!real)
		real = resolve("poll", NULL);
	int r = real(fds, nfds, timeout);
	if (enabled && r > 0 && atomic_load(&fence_dups) > atomic_load(&fence_signals))
		for (nfds_t i = 0; i < nfds; i++)
			if (fds[i].revents & POLLIN)
				fence_check_signal(fds[i].fd);
	return r;
}


// ---- kernel-level implicit sync: no EGL fence is ever created, so the only remaining fence carriers are
// the dma-buf sync_file ioctls and blocking inside GL itself. fastfence here swaps the exported sync fd
// for an already-readable pipe — implicit sync still serialises the actual GPU accesses.

#define DMABUF_EXPORT_SYNC_FILE 0xc0086202u
#define DMABUF_IMPORT_SYNC_FILE 0x40086203u

int ioctl(int fd, unsigned long request, ...) {
	static int (*real)(int, unsigned long, void *);
	va_list ap;
	va_start(ap, request);
	void *arg = va_arg(ap, void *);
	va_end(ap);
	if (!real)
		real = resolve("ioctl", NULL);
	int r = real(fd, request, arg);
	if (!enabled || r != 0 || arg == NULL)
		return r;
	if ((unsigned)request == DMABUF_EXPORT_SYNC_FILE) {
		int *sync_fd = &((int32_t *)arg)[1];
		unsigned n = atomic_fetch_add(&fence_dups, 1);
		if (fastfence) {
			int fds[2];
			if (pipe2(fds, O_CLOEXEC) == 0) {
				char byte = 1;
				if (write(fds[1], &byte, 1) < 0) { /* readable either way once closed */ }
				close(fds[1]);
				close(*sync_fd);
				*sync_fd = fds[0];
				if (n < 8 || n % 600 == 0)
					note("fastfence: export_sync_file #%u -> pre-signaled pipe fd=%d", n, fds[0]);
				return 0;
			}
		}
		fence_ring_add(*sync_fd);
		if (n < 8 || n % 600 == 0)
			note("dmabuf export_sync_file -> fd=%d (#%u)", *sync_fd, n);
	} else if ((unsigned)request == DMABUF_IMPORT_SYNC_FILE) {
		static _Atomic unsigned imports;
		unsigned n = atomic_fetch_add(&imports, 1);
		if (n < 8 || n % 600 == 0)
			note("dmabuf import_sync_file (#%u)", n);
	}
	return r;
}

// Blocking inside GL is the other hiding place: an implicit-sync stall surfaces as a slow flush or wait.
static void (*glflush_from_getproc)(void);

void glFlush(void) {
	static void (*real)(void);
	if (!real) {
		real = glflush_from_getproc;
		if (!real)
			real = (void (*)(void))resolve("glFlush", "libGLESv2.so.2");
		if (!real)
			real = (void (*)(void))resolve("glFlush", "libGL.so.1");
	}
	if (!real)
		return;
	if (!enabled) {
		real();
		return;
	}
	unsigned n = atomic_fetch_add(&flush_calls, 1);
	uint64_t t0 = now_ns();
	real();
	uint64_t took = now_ns() - t0;
	if (took > 2000000 || n < 4 || n % 600 == 0)
		note("glFlush #%u took=%.2fms", n, (double)took / 1e6);
}

static int (*clientwait_from_getproc)(void *, void *, int, uint64_t);

int eglClientWaitSyncKHR(void *dpy, void *sync, int flags, uint64_t timeout) {
	static int (*real)(void *, void *, int, uint64_t);
	if (!real) {
		real = clientwait_from_getproc;
		if (!real)
			real = (int (*)(void *, void *, int, uint64_t))resolve("eglClientWaitSyncKHR", "libEGL.so.1");
	}
	if (!real)
		return 0;
	unsigned n = atomic_fetch_add(&clientwait_calls, 1);
	uint64_t t0 = now_ns();
	int r = real(dpy, sync, flags, timeout);
	uint64_t took = now_ns() - t0;
	if (enabled && (took > 2000000 || n < 4 || n % 600 == 0))
		note("eglClientWaitSync #%u took=%.2fms -> %d", n, (double)took / 1e6, r);
	return r;
}


// ---- explicit-sync completion: NVIDIA's egl-wayland2 registers wp_linux_drm_syncobj_surface_v1 on the
// toplevel for its own EGL swapchain, after which the protocol demands acquire+release points on EVERY
// buffered commit — and GDK/WebKit's own commits carry none, which the compositor punishes by killing the
// connection ("no acquire point is set", the Error 71 crash). The patch captures the manager and surface
// objects from egl-wayland2's marshals, imports two private timelines (one this shim pre-signals for
// acquire — correct for content completed before commit — and one only the compositor signals for
// release), and arms any naked commit right before forwarding it.

typedef union {
	int32_t i;
	uint32_t u;
	int32_t f;
	const char *s;
	void *o;
	uint32_t n;
	void *a;
	int32_t h;
} WlArg;

static const char *const sync_manager_name = "wp_linux_drm_syncobj_manager_v1";
static const char *const sync_surface_name = "wp_linux_drm_syncobj_surface_v1";

struct WlMessage { const char *name; const char *signature; const void **types; };
struct WlInterface {
	const char *name;
	int version;
	int method_count;
	const struct WlMessage *methods;
	int event_count;
	const void *events;
};

static const void *timeline_types[2] = { NULL, NULL };
static const struct WlMessage timeline_requests[1] = { { "destroy", "", timeline_types } };
static const struct WlInterface timeline_interface = {
	"wp_linux_drm_syncobj_timeline_v1", 1, 1, timeline_requests, 0, NULL,
};

static void *sync_manager_proxy;
// Keyed by wayland object id, not proxy pointer: GDK reaches the same wl_surface through queue-wrapper
// proxies, which are distinct pointers sharing the object id.
static struct {
	uint32_t surface_id;
	uint32_t sync_surface_id;
	void *sync_surface;
	int armed;
	int buffer_pending;
} sync_surfaces[8];

static uint32_t (*real_proxy_id)(void *);

// egl-wayland's imported timelines, mirrored as our own syncobj handles so the emulated-vblank thread can
// query exactly when each acquire point (an NVIDIA GPU fence) and release point (KWin's done-signal)
// materialises — separating a late-signaling driver fence from a late-scheduling compositor.
static struct {
	uint32_t proxy_id;
	uint32_t handle;
} watched_timelines[4];
static struct {
	uint32_t timeline_id;
	uint64_t point;
	uint64_t set_ns;
	int is_acquire;
} pending_points[32];
static _Atomic unsigned pending_next;
static int (*drm_syncobj_query)(int, uint32_t *, uint64_t *, uint32_t);
static int pending_fd_to_import = -1;

static _Atomic uint32_t shm_buffer_ids[32];
static _Atomic unsigned shm_buffer_next;

static void shm_buffer_add(uint32_t id) {
	if (id != 0)
		shm_buffer_ids[atomic_fetch_add(&shm_buffer_next, 1) % 32] = id;
}

static int shm_buffer_known(uint32_t id) {
	if (id == 0)
		return 0;
	for (unsigned i = 0; i < 32; i++)
		if (atomic_load(&shm_buffer_ids[i]) == id)
			return 1;
	return 0;
}

static uint32_t wl_id(void *proxy) {
	if (!real_proxy_id)
		real_proxy_id = (uint32_t (*)(void *))resolve("wl_proxy_get_id", "libwayland-client.so.0");
	return real_proxy_id ? real_proxy_id(proxy) : 0;
}

static struct {
	int drm_fd;
	uint32_t acquire_handle;
	uint64_t next_point;
	void *acquire_timeline;
	void *release_timeline;
	int failed;
} sync_state = { .drm_fd = -1 };

static int sync_slot_for_id(uint32_t surface_id) {
	if (surface_id == 0)
		return -1;
	for (unsigned i = 0; i < 8; i++)
		if (sync_surfaces[i].surface_id == surface_id)
			return (int)i;
	return -1;
}

static int sync_open_drm(void) {
	if (sync_state.drm_fd >= 0)
		return 1;
	static const char *nodes[] = { "/dev/dri/renderD128", "/dev/dri/renderD129", "/dev/dri/card1", "/dev/dri/card0" };
	void *libdrm = dlopen("libdrm.so.2", RTLD_LAZY | RTLD_LOCAL);
	int (*syncobj_create)(int, uint32_t, uint32_t *) = libdrm ? dlsym(libdrm, "drmSyncobjCreate") : NULL;
	if (syncobj_create == NULL)
		return 0;
	for (unsigned i = 0; i < sizeof nodes / sizeof nodes[0]; i++) {
		int fd = open(nodes[i], O_RDWR | O_CLOEXEC);
		uint32_t probe = 0;
		if (fd >= 0 && syncobj_create(fd, 0, &probe) == 0) {
			sync_state.drm_fd = fd;
			return 1;
		}
		if (fd >= 0)
			close(fd);
	}
	return 0;
}

static void *sync_import_timeline(int (*syncobj_create)(int, uint32_t, uint32_t *),
	int (*syncobj_to_fd)(int, uint32_t, int *), uint32_t *handle_out) {
	uint32_t handle = 0;
	int sync_fd = -1;
	if (syncobj_create(sync_state.drm_fd, 0, &handle) != 0)
		return NULL;
	if (syncobj_to_fd(sync_state.drm_fd, handle, &sync_fd) != 0 || sync_fd < 0)
		return NULL;
	WlArg args[2];
	args[0].o = NULL;
	args[1].h = sync_fd;
	uint32_t version = real_proxy_version ? real_proxy_version(sync_manager_proxy) : 1;
	void *timeline = real_marshal_flags(sync_manager_proxy, 2 /* import_timeline */,
		&timeline_interface, version, 0, args);
	if (timeline != NULL && handle_out != NULL)
		*handle_out = handle;
	return timeline;
}

static int sync_setup(void) {
	if (sync_state.failed)
		return 0;
	if (sync_state.acquire_timeline != NULL)
		return 1;
	if (sync_manager_proxy == NULL || real_marshal_flags == NULL) {
		sync_state.failed = 1;
		note("syncpatch: bail — manager=%p marshal_flags=%p", sync_manager_proxy, (void *)real_marshal_flags);
		return 0;
	}
	void *libdrm = dlopen("libdrm.so.2", RTLD_LAZY | RTLD_LOCAL);
	int (*syncobj_create)(int, uint32_t, uint32_t *) = libdrm ? dlsym(libdrm, "drmSyncobjCreate") : NULL;
	int (*syncobj_to_fd)(int, uint32_t, int *) = libdrm ? dlsym(libdrm, "drmSyncobjHandleToFD") : NULL;
	if (syncobj_create == NULL || syncobj_to_fd == NULL) {
		sync_state.failed = 1;
		note("syncpatch: bail — libdrm syncobj symbols missing");
		return 0;
	}
	if (!sync_open_drm()) {
		sync_state.failed = 1;
		note("syncpatch: bail — no DRM node accepted a syncobj");
		return 0;
	}
	sync_state.acquire_timeline = sync_import_timeline(syncobj_create, syncobj_to_fd, &sync_state.acquire_handle);
	uint32_t unused = 0;
	sync_state.release_timeline = sync_import_timeline(syncobj_create, syncobj_to_fd, &unused);
	sync_state.next_point = 1;
	if (sync_state.acquire_timeline == NULL || sync_state.release_timeline == NULL) {
		sync_state.failed = 1;
		note("syncpatch: timeline import failed");
		return 0;
	}
	note("syncpatch: private acquire/release timelines imported (drm fd ready)");
	return 1;
}

static void sync_set_point(void *sync_surface, uint32_t opcode, void *timeline, uint64_t point) {
	WlArg args[3];
	args[0].o = timeline;
	args[1].u = (uint32_t)(point >> 32);
	args[2].u = (uint32_t)point;
	if (real_marshal_array_fn != NULL) {
		real_marshal_array_fn(sync_surface, opcode, args);
	} else if (real_marshal_flags != NULL) {
		real_marshal_flags(sync_surface, opcode, NULL,
			real_proxy_version ? real_proxy_version(sync_surface) : 1, 0, args);
	}
}

// Runs before a commit is forwarded: if the surface is under explicit sync and this commit attached a
// buffer without egl-wayland arming it, sign it with a pre-signaled acquire point and a fresh release point.
static void sync_before_commit(void *surface) {
	static _Atomic unsigned commits_seen;
	int slot = sync_slot_for_id(wl_id(surface));
	if (slot < 0 || sync_surfaces[slot].sync_surface == NULL)
		return;
	int naked = sync_surfaces[slot].buffer_pending && !sync_surfaces[slot].armed;
	sync_surfaces[slot].buffer_pending = 0;
	sync_surfaces[slot].armed = 0;
	unsigned seen = atomic_fetch_add(&commits_seen, 1);
	if (seen < 6)
		note("syncpatch: commit on tracked surface (naked=%d, syncpatch=%d)", naked, syncpatch);
	if (!naked || !syncpatch)
		return;
	if (!sync_setup())
		return;
	static int (*timeline_signal)(int, const uint32_t *, uint64_t *, uint32_t);
	if (!timeline_signal) {
		void *libdrm = dlopen("libdrm.so.2", RTLD_LAZY | RTLD_LOCAL);
		timeline_signal = libdrm ? dlsym(libdrm, "drmSyncobjTimelineSignal") : NULL;
	}
	if (timeline_signal == NULL)
		return;
	uint64_t point = sync_state.next_point++;
	if (timeline_signal(sync_state.drm_fd, &sync_state.acquire_handle, &point, 1) != 0) {
		note("syncpatch: pre-signal failed (errno=%d)", errno);
		return;
	}
	sync_set_point(sync_surfaces[slot].sync_surface, 1 /* set_acquire_point */, sync_state.acquire_timeline, point);
	sync_set_point(sync_surfaces[slot].sync_surface, 2 /* set_release_point */, sync_state.release_timeline, point);
	unsigned n = atomic_fetch_add(&sync_injected, 1);
	if (n < 8 || n % 600 == 0)
		note("syncpatch: armed naked commit #%u (acquire point %llu, pre-signaled)", n, (unsigned long long)point);
}

// Observes every marshal (before forwarding) to track the protocol objects and per-surface state.
static int sync_observe(void *proxy, uint32_t opcode, void *args, const char *cls) {
	if (cls == NULL)
		return 0;
	if (strcmp(cls, sync_manager_name) == 0) {
		sync_manager_proxy = proxy;
		if (opcode == 2 && args != NULL && syncpatch)
			pending_fd_to_import = ((WlArg *)args)[1].h;
		return 0;
	}
	if (strcmp(cls, sync_surface_name) == 0) {
		uint32_t id = wl_id(proxy);
		for (unsigned i = 0; i < 8; i++)
			if (sync_surfaces[i].sync_surface_id == id && id != 0) {
				if (opcode == 1)
					sync_surfaces[i].armed = 1;
				else if (opcode == 0)
					sync_surfaces[i].sync_surface = NULL;
			}
		if (syncpatch && (opcode == 1 || opcode == 2) && args != NULL) {
			WlArg *point_args = (WlArg *)args;
			unsigned slot = atomic_fetch_add(&pending_next, 1) % 32;
			pending_points[slot].timeline_id = wl_id(point_args[0].o);
			pending_points[slot].point = ((uint64_t)point_args[1].u << 32) | point_args[2].u;
			pending_points[slot].set_ns = now_ns();
			pending_points[slot].is_acquire = opcode == 1;
		}
		return 0;
	}
	if (strcmp(cls, "wl_surface") == 0) {
		if (opcode == 1 && args != NULL && ((WlArg *)args)[0].o != NULL) {
			int slot = sync_slot_for_id(wl_id(proxy));
			if (slot >= 0) {
				// An SHM buffer can never satisfy explicit sync; a bufferless commit is legal, so the
				// stray cairo frame is dropped rather than armed. Content flows through the EGL swapchain.
				if (syncpatch && sync_surfaces[slot].sync_surface != NULL
					&& shm_buffer_known(wl_id(((WlArg *)args)[0].o))) {
					unsigned n = atomic_fetch_add(&sync_dropped, 1);
					if (n < 8 || n % 600 == 0)
						note("syncpatch: dropped naked SHM attach #%u on wl_surface#%u", n, wl_id(proxy));
					return 1;
				}
				sync_surfaces[slot].buffer_pending = 1;
			}
		} else if (opcode == 6) {
			sync_before_commit(proxy);
		}
	}
	return 0;
}

// After a constructor returns: manager.get_surface pairs the new syncobj surface with its wl_surface.
static void sync_observe_created(void *proxy, uint32_t opcode, void *args, void *created, const char *cls) {
	if (cls == NULL || created == NULL || args == NULL)
		return;
	if (strcmp(cls, "wl_shm_pool") == 0 && opcode == 0) {
		shm_buffer_add(wl_id(created));
		return;
	}
	if (strcmp(cls, sync_manager_name) == 0 && opcode == 2 && pending_fd_to_import >= 0) {
		int fd = pending_fd_to_import;
		pending_fd_to_import = -1;
		if (sync_open_drm()) {
			static int (*fd_to_handle)(int, int, uint32_t *);
			if (!fd_to_handle) {
				void *libdrm = dlopen("libdrm.so.2", RTLD_LAZY | RTLD_LOCAL);
				fd_to_handle = libdrm ? dlsym(libdrm, "drmSyncobjFDToHandle") : NULL;
				drm_syncobj_query = libdrm ? dlsym(libdrm, "drmSyncobjQuery") : NULL;
			}
			uint32_t handle = 0;
			if (fd_to_handle != NULL && fd_to_handle(sync_state.drm_fd, fd, &handle) == 0)
				for (unsigned i = 0; i < 4; i++)
					if (watched_timelines[i].proxy_id == 0) {
						watched_timelines[i].proxy_id = wl_id(created);
						watched_timelines[i].handle = handle;
						note("syncpatch: watching egl-wayland timeline #%u (syncobj handle %u)",
							watched_timelines[i].proxy_id, handle);
						break;
					}
		}
		return;
	}
	if (strcmp(cls, sync_manager_name) == 0 && opcode == 1) {
		uint32_t surface_id = wl_id(((WlArg *)args)[1].o);
		for (unsigned i = 0; i < 8; i++)
			if (sync_surfaces[i].surface_id == surface_id || sync_surfaces[i].surface_id == 0) {
				sync_surfaces[i].surface_id = surface_id;
				sync_surfaces[i].sync_surface = created;
				sync_surfaces[i].sync_surface_id = wl_id(created);
				note("syncpatch: explicit sync registered on wl_surface#%u (syncobj surface #%u)",
					surface_id, sync_surfaces[i].sync_surface_id);
				break;
			}
	}
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
	if (syncpatch && drm_syncobj_query != NULL && sync_state.drm_fd >= 0) {
		uint64_t t = now_ns();
		for (unsigned i = 0; i < 32; i++) {
			if (pending_points[i].timeline_id == 0)
				continue;
			for (unsigned w = 0; w < 4; w++) {
				if (watched_timelines[w].proxy_id != pending_points[i].timeline_id)
					continue;
				uint32_t handle = watched_timelines[w].handle;
				uint64_t value = 0;
				if (drm_syncobj_query(sync_state.drm_fd, &handle, &value, 1) == 0
					&& value >= pending_points[i].point) {
					static _Atomic unsigned observed;
					unsigned n = atomic_fetch_add(&observed, 1);
					if (n < 16 || n % 240 == 0)
						note("%s point %llu signaled %.2fms after set",
							pending_points[i].is_acquire ? "acquire" : "release",
							(unsigned long long)pending_points[i].point,
							(double)(t - pending_points[i].set_ns) / 1e6);
					pending_points[i].timeline_id = 0;
				}
			}
		}
	}
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

// GDK and drivers fetch EGL entry points through eglGetProcAddress, whose raw driver pointers bypass the
// PLT — the reason no swap ever appeared in earlier traces. Interposing it routes the two calls that
// matter back through the wrappers, and the wrappers prefer the driver pointer it captured.
static unsigned int (*swap_from_getproc)(void *, void *);
static unsigned int (*interval_from_getproc)(void *, int);

unsigned int eglSwapBuffers(void *display, void *surface) {
	static unsigned int (*fallback)(void *, void *);
	static unsigned int (*fallback_interval)(void *, int);
	unsigned int (*real)(void *, void *) = swap_from_getproc;
	if (real == NULL) {
		if (!fallback)
			fallback = resolve("eglSwapBuffers", "libEGL.so.1");
		real = fallback;
	}
	if (real == NULL)
		return 0;
	if (!enabled)
		return real(display, surface);
	if (swap0 && !atomic_exchange(&swap0_done, 1)) {
		unsigned int (*set)(void *, int) = interval_from_getproc;
		if (set == NULL) {
			if (!fallback_interval)
				fallback_interval = resolve("eglSwapInterval", "libEGL.so.1");
			set = fallback_interval;
		}
		if (set)
			note("forced eglSwapInterval(0) -> %u", set(display, 0));
	}
	unsigned n = atomic_fetch_add(&swap_calls, 1);
	uint64_t t0 = now_ns();
	unsigned int ok = real(display, surface);
	uint64_t t1 = now_ns();
	uint64_t prev = atomic_exchange(&swap_last, t1);
	if (n < 12 || n % 240 == 0)
		note("eglSwapBuffers #%u dt=%.2fms blocked=%.2fms", n,
			prev ? (double)(t1 - prev) / 1e6 : 0.0, (double)(t1 - t0) / 1e6);
	return ok;
}

unsigned int eglSwapInterval(void *display, int interval) {
	static unsigned int (*fallback)(void *, int);
	unsigned int (*real)(void *, int) = interval_from_getproc;
	if (real == NULL) {
		if (!fallback)
			fallback = resolve("eglSwapInterval", "libEGL.so.1");
		real = fallback;
	}
	if (real == NULL)
		return 0;
	if (enabled)
		note("app eglSwapInterval(%d)%s", interval, swap0 ? " -> 0" : "");
	return real(display, swap0 ? 0 : interval);
}

void *eglGetProcAddress(const char *name) {
	static void *(*real)(const char *);
	if (!real)
		real = resolve("eglGetProcAddress", "libEGL.so.1");
	if (real == NULL)
		return NULL;
	void *p = real(name);
	if (p == NULL || name == NULL)
		return p;
	if (strcmp(name, "eglSwapBuffers") == 0) {
		swap_from_getproc = (unsigned int (*)(void *, void *))p;
		if (enabled)
			note("eglGetProcAddress(eglSwapBuffers) -> interposed");
		return (void *)eglSwapBuffers;
	}
	if (strcmp(name, "eglDupNativeFenceFDANDROID") == 0) {
		dupfence_from_getproc = (int (*)(void *, void *))p;
		if (enabled)
			note("eglGetProcAddress(eglDupNativeFenceFDANDROID) -> interposed");
		return (void *)eglDupNativeFenceFDANDROID;
	}
	if (strcmp(name, "glFinish") == 0) {
		glfinish_from_getproc = (void (*)(void))p;
		return p;
	}
	if (strcmp(name, "glFlush") == 0) {
		glflush_from_getproc = (void (*)(void))p;
		return (void *)glFlush;
	}
	if (strcmp(name, "eglClientWaitSyncKHR") == 0 || strcmp(name, "eglClientWaitSync") == 0) {
		clientwait_from_getproc = (int (*)(void *, void *, int, uint64_t))p;
		return (void *)eglClientWaitSyncKHR;
	}
	if (strcmp(name, "eglSwapInterval") == 0) {
		interval_from_getproc = (unsigned int (*)(void *, int))p;
		if (enabled)
			note("eglGetProcAddress(eglSwapInterval) -> interposed");
		return (void *)eglSwapInterval;
	}
	return p;
}

int gdk_monitor_get_refresh_rate(void *monitor) {
	static int (*real)(void *);
	static _Atomic int last = -1;
	if (!real)
		real = resolve("gdk_monitor_get_refresh_rate", "libgdk-3.so.0");
	if (real == NULL)
		return 0;
	int rate = real(monitor);
	if (enabled && atomic_exchange(&last, rate) != rate) {
		note("gdk_monitor_get_refresh_rate -> %d mHz", rate);
		// The vblank monitor reads this during construction, before its first wait — so the emulation grid
		// can take its rate from the display itself instead of a hardcoded guess.
		if (!hz_from_env && rate > 1000) {
			hz = rate / 1000.0;
			note("emulation grid <- %.3f Hz (from gdk)", hz);
		}
	}
	return rate;
}

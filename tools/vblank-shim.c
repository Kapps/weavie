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

static int enabled, fix_drm, force_drm, fix_timer, swap0, steer, hz_from_env, fastfence;
static double hz = 240.0;
static _Atomic unsigned drm_calls, drm_fails, timer_hits, swap_calls;
static _Atomic unsigned commit_calls, frame_reqs, attach_calls, shm_buffers, dmabuf_buffers, draw_calls;
static _Atomic unsigned framecb_events, release_events, fence_dups, fence_signals;
static _Atomic unsigned flush_calls, clientwait_calls;
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

static int frame_proxy_known(void *proxy) {
	for (unsigned i = 0; i < 64; i++)
		if (atomic_load(&frame_proxies[i]) == proxy)
			return 1;
	return 0;
}
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
	fastfence = (e = getenv("VBLANK_SHIM_FASTFENCE")) && *e && strcmp(e, "0");
	if ((e = getenv("VBLANK_SHIM_HZ")) && atof(e) > 0) {
		hz = atof(e);
		hz_from_env = 1;
	}
	note("active in '%s' (fix_drm=%d force_drm=%d fix_timer=%d swap0=%d steer=%d fastfence=%d hz=%.3f)",
		comm, fix_drm, force_drm, fix_timer, swap0, steer, fastfence, hz);
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

static void wl_track(void *proxy, uint32_t opcode) {
	static const char *(*get_class)(void *);
	if (!enabled)
		return;
	if (!get_class)
		get_class = resolve("wl_proxy_get_class", "libwayland-client.so.0");
	if (get_class == NULL)
		return;
	const char *cls = get_class(proxy);
	if (cls == NULL)
		return;
	if (strcmp(cls, "wl_surface") == 0) {
		if (opcode == 6) {
			unsigned n = atomic_fetch_add(&commit_calls, 1);
			uint64_t t = now_ns();
			uint64_t prev = atomic_exchange(&commit_last, t);
			if (n < 12 || n % 240 == 0)
				note("wl_surface.commit #%u dt=%.2fms", n, prev ? (double)(t - prev) / 1e6 : 0.0);
		} else if (opcode == 3) {
			atomic_fetch_add(&frame_reqs, 1);
		} else if (opcode == 1) {
			atomic_fetch_add(&attach_calls, 1);
		}
	} else if (strcmp(cls, "wl_shm_pool") == 0 && opcode == 0) {
		atomic_fetch_add(&shm_buffers, 1);
	} else if (strcmp(cls, "zwp_linux_buffer_params_v1") == 0 && (opcode == 1 || opcode == 2)) {
		atomic_fetch_add(&dmabuf_buffers, 1);
	}
}

void wl_proxy_marshal_array(void *proxy, uint32_t opcode, void *args) {
	static void (*real)(void *, uint32_t, void *);
	if (!real)
		real = resolve("wl_proxy_marshal_array", "libwayland-client.so.0");
	if (real == NULL)
		return;
	wl_track(proxy, opcode);
	real(proxy, opcode, args);
}

void *wl_proxy_marshal_array_constructor(void *proxy, uint32_t opcode, void *args, const void *interface) {
	static void *(*real)(void *, uint32_t, void *, const void *);
	if (!real)
		real = resolve("wl_proxy_marshal_array_constructor", "libwayland-client.so.0");
	if (real == NULL)
		return NULL;
	wl_track(proxy, opcode);
	void *created = real(proxy, opcode, args, interface);
	if (enabled && opcode == 3 && created != NULL)
		frame_proxy_add(created);
	return created;
}

void *wl_proxy_marshal_array_constructor_versioned(void *proxy, uint32_t opcode, void *args,
	const void *interface, uint32_t version) {
	static void *(*real)(void *, uint32_t, void *, const void *, uint32_t);
	if (!real)
		real = resolve("wl_proxy_marshal_array_constructor_versioned", "libwayland-client.so.0");
	if (real == NULL)
		return NULL;
	wl_track(proxy, opcode);
	void *created = real(proxy, opcode, args, interface, version);
	if (enabled && opcode == 3 && created != NULL)
		frame_proxy_add(created);
	return created;
}

void *wl_proxy_marshal_array_flags(void *proxy, uint32_t opcode, const void *interface, uint32_t version,
	uint32_t flags, void *args) {
	static void *(*real)(void *, uint32_t, const void *, uint32_t, uint32_t, void *);
	if (!real)
		real = resolve("wl_proxy_marshal_array_flags", "libwayland-client.so.0");
	if (real == NULL)
		return NULL;
	wl_track(proxy, opcode);
	void *created = real(proxy, opcode, interface, version, flags, args);
	if (enabled && opcode == 3 && created != NULL)
		frame_proxy_add(created);
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
	if (n < 12 || n % 240 == 0)
		note("frame callback #%u dt=%.2fms latency-after-commit=%.2fms", n,
			prev ? (double)(t - prev) / 1e6 : 0.0, (double)(t - atomic_load(&commit_last)) / 1e6);
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

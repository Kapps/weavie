// Repairs the display cadence WebKitGTK derives for the page it renders.
//
// WebKitGTK ticks its rendering updates from a DRM vblank monitor that only constructs when a connected
// connector's EDID millimetres *exactly* equal the size the compositor reports, and only ticks when the
// driver implements the legacy vblank ioctl. Neither holds on a common Wayland desktop: EDID stores the
// physical size twice — whole centimetres in the base block (what the kernel puts on the connector) and
// exact millimetres in the detailed timing descriptor (what a compositor hands the toolkit) — and drivers
// such as nvidia-drm ship the ioctl disabled. WebKit then silently paces every page at a hardcoded 60fps.
//
// This library sits in front of libdrm in the host process and answers both questions correctly. It is
// inert until the host registers the monitors its toolkit reports.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <xf86drm.h>
#include <xf86drmMode.h>

// EDID's base block carries the size in whole centimetres, so a connector and a compositor describing the
// same panel can disagree by up to one centimetre in each axis and by no more.
#define CENTIMETRE_MM 10

typedef struct {
	int width_mm;
	int height_mm;
	unsigned refresh_millihz;
} Monitor;

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static Monitor *monitors;
static unsigned monitor_count;
// Refresh rate per CRTC index, learned while reconciling that CRTC's connector; index 0 means "not learned".
static unsigned *crtc_refresh_millihz;
static unsigned crtc_refresh_count;

static void *real(const char *symbol) {
	void *found = dlsym(RTLD_NEXT, symbol);
	if (found == NULL) {
		void *libdrm = dlopen("libdrm.so.2", RTLD_LAZY | RTLD_LOCAL);
		if (libdrm != NULL)
			found = dlsym(libdrm, symbol);
	}
	return found;
}

/// Registers a monitor as the toolkit reports it. Call once per monitor, after clearing.
void weavie_display_sync_add_monitor(int width_mm, int height_mm, unsigned refresh_millihz) {
	pthread_mutex_lock(&lock);
	Monitor *grown = realloc(monitors, (monitor_count + 1) * sizeof(Monitor));
	if (grown != NULL) {
		monitors = grown;
		monitors[monitor_count++] = (Monitor){ width_mm, height_mm, refresh_millihz };
	}
	pthread_mutex_unlock(&lock);
}

/// Drops everything learned about the displays, so a display change can register the current set.
void weavie_display_sync_clear_monitors(void) {
	pthread_mutex_lock(&lock);
	free(monitors);
	monitors = NULL;
	monitor_count = 0;
	free(crtc_refresh_millihz);
	crtc_refresh_millihz = NULL;
	crtc_refresh_count = 0;
	pthread_mutex_unlock(&lock);
}

// The registered monitor describing this connector, or NULL when none does.
static const Monitor *monitor_for(uint32_t width_mm, uint32_t height_mm) {
	for (unsigned i = 0; i < monitor_count; i++) {
		int dw = (int)width_mm - monitors[i].width_mm;
		int dh = (int)height_mm - monitors[i].height_mm;
		if (dw < 0)
			dw = -dw;
		if (dh < 0)
			dh = -dh;
		if (dw <= CENTIMETRE_MM && dh <= CENTIMETRE_MM)
			return &monitors[i];
	}
	return NULL;
}

// Records the refresh rate to pace this connector's CRTC at, so an emulated wait uses the rate of the
// display it is asked about rather than a single global guess.
static void remember_crtc_refresh(int fd, drmModeConnectorPtr connector, unsigned refresh_millihz) {
	static drmModeResPtr (*get_resources)(int);
	static void (*free_resources)(drmModeResPtr);
	static drmModeEncoderPtr (*get_encoder)(int, uint32_t);
	static void (*free_encoder)(drmModeEncoderPtr);
	if (get_resources == NULL) {
		get_resources = real("drmModeGetResources");
		free_resources = real("drmModeFreeResources");
		get_encoder = real("drmModeGetEncoder");
		free_encoder = real("drmModeFreeEncoder");
	}
	if (get_resources == NULL || get_encoder == NULL || connector->encoder_id == 0)
		return;

	drmModeResPtr resources = get_resources(fd);
	if (resources == NULL)
		return;
	drmModeEncoderPtr encoder = get_encoder(fd, connector->encoder_id);
	if (encoder != NULL) {
		for (int i = 0; i < resources->count_crtcs; i++) {
			if (resources->crtcs[i] != encoder->crtc_id)
				continue;
			if ((unsigned)i >= crtc_refresh_count) {
				unsigned grown_count = (unsigned)i + 1;
				unsigned *grown = realloc(crtc_refresh_millihz, grown_count * sizeof(unsigned));
				if (grown == NULL)
					break;
				memset(grown + crtc_refresh_count, 0, (grown_count - crtc_refresh_count) * sizeof(unsigned));
				crtc_refresh_millihz = grown;
				crtc_refresh_count = grown_count;
			}
			crtc_refresh_millihz[i] = refresh_millihz;
			break;
		}
		free_encoder(encoder);
	}
	free_resources(resources);
}

drmModeConnectorPtr drmModeGetConnector(int fd, uint32_t connector_id) {
	static drmModeConnectorPtr (*forward)(int, uint32_t);
	if (forward == NULL)
		forward = real("drmModeGetConnector");
	if (forward == NULL) {
		errno = ENOSYS;
		return NULL;
	}

	drmModeConnectorPtr connector = forward(fd, connector_id);
	if (connector == NULL || connector->connection != DRM_MODE_CONNECTED)
		return connector;

	pthread_mutex_lock(&lock);
	const Monitor *monitor = monitor_for(connector->mmWidth, connector->mmHeight);
	if (monitor != NULL) {
		connector->mmWidth = (uint32_t)monitor->width_mm;
		connector->mmHeight = (uint32_t)monitor->height_mm;
		remember_crtc_refresh(fd, connector, monitor->refresh_millihz);
	}
	pthread_mutex_unlock(&lock);
	return connector;
}

// The CRTC index libdrm encodes into a vblank request's type field.
static unsigned crtc_index_of(unsigned type) {
	unsigned high = (type & DRM_VBLANK_HIGH_CRTC_MASK) >> DRM_VBLANK_HIGH_CRTC_SHIFT;
	if (high != 0)
		return high;
	return (type & DRM_VBLANK_SECONDARY) != 0 ? 1 : 0;
}

static unsigned refresh_for(unsigned type) {
	unsigned index = crtc_index_of(type);
	unsigned millihz = 0;
	pthread_mutex_lock(&lock);
	if (index < crtc_refresh_count)
		millihz = crtc_refresh_millihz[index];
	pthread_mutex_unlock(&lock);
	return millihz;
}

int drmWaitVBlank(int fd, drmVBlankPtr request) {
	static int (*forward)(int, drmVBlankPtr);
	static _Atomic unsigned emulated_sequence;
	if (forward == NULL)
		forward = real("drmWaitVBlank");

	unsigned type = request->request.type;
	if (forward != NULL && forward(fd, request) == 0)
		return 0;

	// The driver has no vblank to wait on. Pace on the display's own period instead of letting WebKit fall
	// back to its hardcoded 60fps timer; the compositor still vsyncs what this cadence produces.
	unsigned millihz = refresh_for(type);
	if (millihz == 0)
		return -1;

	uint64_t period_ns = 1000000000000ull / millihz;
	struct timespec now;
	clock_gettime(CLOCK_MONOTONIC, &now);
	uint64_t target = ((((uint64_t)now.tv_sec * 1000000000ull + now.tv_nsec) / period_ns) + 1) * period_ns;
	struct timespec until = { .tv_sec = (time_t)(target / 1000000000ull), .tv_nsec = (long)(target % 1000000000ull) };
	while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &until, NULL) == EINTR) { }

	request->reply.type = type;
	request->reply.sequence = atomic_fetch_add(&emulated_sequence, 1) + 1;
	request->reply.tval_sec = (long)(target / 1000000000ull);
	request->reply.tval_usec = (long)((target % 1000000000ull) / 1000);
	return 0;
}

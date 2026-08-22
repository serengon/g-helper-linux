/* NVIDIA daemon/rmmod/smi/modprobe/vulkan/egl/drm-notify/nvml for gpu-helper. */
#include "gpu-helper.h"

/* ---------- daemon (systemctl) ---------- */

int do_daemon(int argc, char **argv)
{
    if (argc != 4)
    {
        fprintf(stderr, "usage: daemon <stop|start|reset-failed|try-restart> <unit>\n");
        return 1;
    }
    const char *verb = argv[2];
    const char *unit = argv[3];
    /* try-restart is a no-op when the unit is not already running, so it
       cannot start a daemon the user deliberately left off. */
    if (strcmp(verb, "stop") != 0 && strcmp(verb, "start") != 0 &&
        strcmp(verb, "reset-failed") != 0 && strcmp(verb, "try-restart") != 0)
    {
        fprintf(stderr, "daemon: verb not permitted\n");
        return 1;
    }
    if (strcmp(unit, "nvidia-powerd") != 0 && strcmp(unit, "nvidia-persistenced") != 0)
    {
        glog(LOG_WARNING, "daemon: unit not permitted: %s", unit);
        fprintf(stderr, "daemon: unit not permitted\n");
        return 1;
    }
    glog(LOG_INFO, "daemon: systemctl %s %s", verb, unit);
    char *const a[] = {"systemctl", (char *)verb, (char *)unit, NULL};
    return exec_tool("systemctl", a);
}

/* ---------- rmmod ---------- */

int do_rmmod(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: rmmod <module>\n");
        return 1;
    }
    const char *m = argv[2];
    static const char *allowed[] = {
        "nvidia_drm", "nvidia_modeset", "nvidia_uvm", "nvidia", "nvidia_wmi_ec_backlight"};
    int ok = 0;
    for (size_t i = 0; i < sizeof(allowed) / sizeof(allowed[0]); i++)
        if (strcmp(m, allowed[i]) == 0)
        {
            ok = 1;
            break;
        }
    if (!ok)
    {
        glog(LOG_WARNING, "rmmod: module not permitted: %s", m);
        fprintf(stderr, "rmmod: module not permitted\n");
        return 1;
    }
    glog(LOG_INFO, "rmmod %s", m);
    char *const a[] = {"rmmod", (char *)m, NULL};
    return exec_tool("rmmod", a);
}

/* ---------- vulkan icd (nvidia) ---------- */

#define VK_NVIDIA_ICD "/usr/share/vulkan/icd.d/nvidia_icd.json"
#define VK_NVIDIA_ICD_INACTIVE "/usr/share/vulkan/icd.d/nvidia_icd.json_inactive"

/* Hide/show the NVIDIA Vulkan ICD manifest so Vulkan cleanly falls back to the
 * iGPU while the dGPU is disabled (Eco). Hardcoded path; hide<->show only. */
int do_vulkan_icd(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: vulkan-icd <hide|show>\n");
        return 1;
    }
    const char *src, *dst;
    if (strcmp(argv[2], "hide") == 0)
    {
        src = VK_NVIDIA_ICD;
        dst = VK_NVIDIA_ICD_INACTIVE;
    }
    else if (strcmp(argv[2], "show") == 0)
    {
        src = VK_NVIDIA_ICD_INACTIVE;
        dst = VK_NVIDIA_ICD;
    }
    else
    {
        fprintf(stderr, "vulkan-icd: arg must be hide or show\n");
        return 1;
    }

    if (access(src, F_OK) != 0)
    {
        /* Already in the desired state (or no NVIDIA ICD present): no-op. */
        glog(LOG_INFO, "vulkan-icd %s: %s absent, nothing to do", argv[2], src);
        return 0;
    }
    if (rename(src, dst) != 0)
    {
        glog(LOG_ERR, "vulkan-icd %s: rename %s -> %s failed: %s", argv[2], src, dst, strerror(errno));
        fprintf(stderr, "vulkan-icd: rename %s -> %s: %s\n", src, dst, strerror(errno));
        return 3;
    }
    glog(LOG_INFO, "vulkan-icd %s: %s -> %s", argv[2], src, dst);
    return 0;
}

/* ---------- EGL vendor (nvidia) ---------- */

static const char *egl_nvidia_paths[] = {
    "/usr/share/glvnd/egl_vendor.d/10_nvidia.json",
    "/usr/share/glvnd/egl_vendor.d/10_nvidia_wayland.json",
    NULL,
};

int do_egl_vendor(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: egl-vendor <hide|show>\n");
        return 1;
    }
    int hide;
    if (strcmp(argv[2], "hide") == 0)
        hide = 1;
    else if (strcmp(argv[2], "show") == 0)
        hide = 0;
    else
    {
        fprintf(stderr, "egl-vendor: arg must be hide or show\n");
        return 1;
    }

    int acted = 0;
    for (const char **p = egl_nvidia_paths; *p; p++)
    {
        char inactive[PATH_BUF_SIZE];
        snprintf(inactive, sizeof(inactive), "%s_inactive", *p);
        const char *src = hide ? *p : inactive;
        const char *dst = hide ? inactive : *p;
        if (access(src, F_OK) != 0)
            continue;
        if (rename(src, dst) != 0)
        {
            glog(LOG_ERR, "egl-vendor %s: rename %s -> %s: %s", argv[2], src, dst, strerror(errno));
            fprintf(stderr, "egl-vendor: rename %s -> %s: %s\n", src, dst, strerror(errno));
        }
        else
        {
            glog(LOG_INFO, "egl-vendor %s: %s -> %s", argv[2], src, dst);
            acted++;
        }
    }
    if (acted == 0)
        glog(LOG_INFO, "egl-vendor %s: no files to %s", argv[2], argv[2]);
    return 0;
}

/* ---------- DRM compositor notify ---------- */

/* Write a synthetic "remove" uevent to the DRM card sysfs node for a PCI BDF,
 * so compositors (KWin, mutter) release /dev/dri/cardN gracefully before the
 * real driver unbind. Refuses to signal if no non-nvidia DRM card exists
 * (would crash a single-GPU compositor). */
int do_drm_notify_remove(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: drm-notify-remove <bdf>\n");
        return 1;
    }
    const char *bdf = argv[2];
    if (!valid_bdf(bdf))
    {
        fprintf(stderr, "drm-notify-remove: invalid bdf\n");
        return 1;
    }

    /* Safety: verify a non-nvidia DRM card exists (the iGPU). Signaling
     * removal of the primary/only GPU crashes KWin (QCoreApplication::exit). */
    int non_nvidia_found = 0;
    DIR *drmdir = opendir("/sys/class/drm");
    if (drmdir)
    {
        struct dirent *e;
        char path[PATH_BUF_SIZE];
        char buf[32];
        while ((e = readdir(drmdir)) != NULL)
        {
            if (strncmp(e->d_name, "card", 4) != 0 || !isdigit((unsigned char)e->d_name[4]) || strchr(e->d_name + 4, '-') != NULL)
                continue;
            snprintf(path, sizeof(path), "/sys/class/drm/%s/device/vendor", e->d_name);
            FILE *f = fopen(path, "r");
            if (!f)
                continue;
            int nv = (fgets(buf, sizeof(buf), f) && strncmp(buf, "0x10de", 6) == 0);
            fclose(f);
            if (!nv)
            {
                non_nvidia_found = 1;
                break;
            }
        }
        closedir(drmdir);
    }

    if (!non_nvidia_found)
    {
        glog(LOG_WARNING, "drm-notify-remove: no non-nvidia DRM card, skipping");
        fprintf(stderr, "drm-notify-remove: no non-nvidia DRM card (would crash compositor)\n");
        return 2;
    }

    char drmpath[PATH_BUF_SIZE];
    snprintf(drmpath, sizeof(drmpath), "/sys/bus/pci/devices/%s/drm", bdf);
    DIR *dir = opendir(drmpath);
    if (!dir)
    {
        glog(LOG_INFO, "drm-notify-remove %s: no drm dir (driver not bound?)", bdf);
        return 0;
    }

    int signaled = 0;
    struct dirent *de;
    while ((de = readdir(dir)) != NULL)
    {
        if (strncmp(de->d_name, "card", 4) != 0 || !isdigit((unsigned char)de->d_name[4]) || strchr(de->d_name + 4, '-') != NULL)
            continue;

        char uevent[1024];
        snprintf(uevent, sizeof(uevent), "%s/%.255s/uevent", drmpath, de->d_name);
        int fd = open(uevent, O_WRONLY);
        if (fd < 0)
        {
            glog(LOG_WARNING, "drm-notify-remove %s/%s: open: %s", bdf, de->d_name, strerror(errno));
            continue;
        }
        if (write(fd, "remove", 6) < 0)
            glog(LOG_WARNING, "drm-notify-remove %s/%s: write: %s", bdf, de->d_name, strerror(errno));
        else
        {
            glog(LOG_INFO, "drm-notify-remove: signaled %s for %s", de->d_name, bdf);
            printf("%s\n", de->d_name);
            signaled++;
        }
        close(fd);
    }
    closedir(dir);

    if (signaled == 0)
        glog(LOG_INFO, "drm-notify-remove %s: no card entries", bdf);

    /* PCI device uevent: notify nvidia EGL/Vulkan/NVML to release /dev/nvidia* FDs+mmaps. */
    {
        char pci_uevent[PATH_BUF_SIZE];
        snprintf(pci_uevent, sizeof(pci_uevent),
                 "/sys/bus/pci/devices/%s/uevent", bdf);
        int pfd = open(pci_uevent, O_WRONLY);
        if (pfd < 0)
        {
            glog(LOG_WARNING, "drm-notify-remove %s: pci uevent open: %s",
                 bdf, strerror(errno));
        }
        else
        {
            if (write(pfd, "remove", 6) < 0)
                glog(LOG_WARNING, "drm-notify-remove %s: pci uevent write: %s",
                     bdf, strerror(errno));
            else
            {
                glog(LOG_INFO, "drm-notify-remove: pci uevent signaled for %s",
                     bdf);
                printf("pci\n");
                signaled++;
            }
            close(pfd);
        }
    }

    return 0;
}

/* ---------- nvidia-refcnt-holders ---------- */

/* Dump nvidia module refcnt + every process holding /dev/nvidia* FDs or mmaps. */
int do_nvidia_refcnt_holders(void)
{
    /* Print module refcnt first. */
    {
        FILE *f = fopen("/sys/module/nvidia/refcnt", "r");
        if (f)
        {
            int rc;
            if (fscanf(f, "%d", &rc) == 1)
                printf("refcnt=%d\n", rc);
            fclose(f);
        }
        else
        {
            printf("refcnt=?\n");
        }
    }

    DIR *proc = opendir("/proc");
    if (!proc)
        return 0;

    struct dirent *pe;
    while ((pe = readdir(proc)) != NULL)
    {
        if (!isdigit((unsigned char)pe->d_name[0]))
            continue;
        int pid = atoi(pe->d_name);
        if (pid <= 0)
            continue;

        int nv_fds = 0;
        int nv_maps = 0;

        /* Count /dev/nvidia* FDs. */
        {
            char fddir[64];
            snprintf(fddir, sizeof(fddir), "/proc/%d/fd", pid);
            DIR *dd = opendir(fddir);
            if (dd)
            {
                struct dirent *fe;
                while ((fe = readdir(dd)) != NULL)
                {
                    char link[PATH_BUF_SIZE];
                    char fdpath[PATH_BUF_SIZE];
                    snprintf(fdpath, sizeof(fdpath), "%s/%s", fddir, fe->d_name);
                    ssize_t n = readlink(fdpath, link, sizeof(link) - 1);
                    if (n > 0)
                    {
                        link[n] = '\0';
                        if (strncmp(link, NVIDIA_PREFIX, NVIDIA_PREFIX_LEN) == 0)
                            nv_fds++;
                    }
                }
                closedir(dd);
            }
        }

        /* Count /dev/nvidia* memory mappings. */
        {
            char mapspath[64];
            snprintf(mapspath, sizeof(mapspath), "/proc/%d/maps", pid);
            FILE *mf = fopen(mapspath, "r");
            if (mf)
            {
                char line[MAPS_LINE_SIZE];
                while (fgets(line, sizeof(line), mf) != NULL)
                {
                    if (strstr(line, NVIDIA_PREFIX) != NULL)
                        nv_maps++;
                }
                fclose(mf);
            }
        }

        if (nv_fds == 0 && nv_maps == 0)
            continue;

        /* Read comm. */
        char comm[COMM_BUF_SIZE] = "?";
        {
            char cpath[64];
            snprintf(cpath, sizeof(cpath), "/proc/%d/comm", pid);
            FILE *cf = fopen(cpath, "r");
            if (cf)
            {
                if (fgets(comm, sizeof(comm), cf))
                {
                    char *nl = strchr(comm, '\n');
                    if (nl)
                        *nl = '\0';
                }
                fclose(cf);
            }
        }

        /* Read UID. */
        unsigned int uid = (unsigned int)-1;
        {
            char spath[64];
            snprintf(spath, sizeof(spath), "/proc/%d/status", pid);
            FILE *sf = fopen(spath, "r");
            if (sf)
            {
                char sline[256];
                while (fgets(sline, sizeof(sline), sf))
                {
                    if (strncmp(sline, "Uid:", 4) == 0)
                    {
                        sscanf(sline + 4, "%u", &uid);
                        break;
                    }
                }
                fclose(sf);
            }
        }

        /* Read service unit from cgroup. */
        char unit[UNIT_BUF_SIZE] = "-";
        {
            char cgpath[64];
            snprintf(cgpath, sizeof(cgpath), "/proc/%d/cgroup", pid);
            FILE *cgf = fopen(cgpath, "r");
            if (cgf)
            {
                char cgline[CGROUP_BUF_SIZE];
                while (fgets(cgline, sizeof(cgline), cgf))
                {
                    /* Unified hierarchy: "0::/user.slice/.../foo.service" */
                    char *svc = strstr(cgline, ".service");
                    if (svc)
                    {
                        /* Walk backwards to find the unit name start. */
                        char *p = svc;
                        while (p > cgline && *(p - 1) != '/')
                            p--;
                        size_t len = (size_t)(svc + 8 - p);
                        if (len >= sizeof(unit))
                            len = sizeof(unit) - 1;
                        memcpy(unit, p, len);
                        unit[len] = '\0';
                        break;
                    }
                }
                fclose(cgf);
            }
        }

        printf("%d\t%s\t%u\t%d\t%d\t%s\n", pid, comm, uid, nv_fds, nv_maps, unit);
    }
    closedir(proc);
    return 0;
}

/* ---------- nvidia-smi ---------- */

int do_smi(int argc, char **argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: smi <flag> [value]\n");
        return 1;
    }
    const char *flag = argv[2];
    if (strcmp(flag, "-pl") != 0 && strcmp(flag, "-lgc") != 0 && strcmp(flag, "-rgc") != 0 && strcmp(flag, "-lmc") != 0 && strcmp(flag, "-rmc") != 0)
    {
        glog(LOG_WARNING, "smi: flag not permitted: %s", flag);
        fprintf(stderr, "smi: flag not permitted\n");
        return 1;
    }
    /* nvidia-smi <flag> [value] - at most one value argument. */
    if (argc > 4)
    {
        fprintf(stderr, "smi: too many args\n");
        return 1;
    }
    glog(LOG_INFO, "smi %s%s%s", flag, argc == 4 ? " " : "", argc == 4 ? argv[3] : "");
    char *a[4];
    a[0] = "nvidia-smi";
    a[1] = (char *)flag;
    if (argc == 4)
    {
        a[2] = argv[3];
        a[3] = NULL;
    }
    else
    {
        a[2] = NULL;
    }
    return exec_tool("nvidia-smi", a);
}

/* ---------- modprobe ---------- */

int do_modprobe(int argc, char **argv)
{
    /* Permitted exactly: uvcvideo | -r uvcvideo | nvidia-wmi-ec-backlight | amdgpu | nvidia */
    int ok = 0;
    if (argc == 3 && strcmp(argv[2], "uvcvideo") == 0)
        ok = 1;
    else if (argc == 3 && strcmp(argv[2], "nvidia-wmi-ec-backlight") == 0)
        ok = 1;
    else if (argc == 3 && strcmp(argv[2], "amdgpu") == 0)
        ok = 1;
    else if (argc == 3 && strcmp(argv[2], "nvidia") == 0)
        ok = 1;
    else if (argc == 4 && strcmp(argv[2], "-r") == 0 && strcmp(argv[3], "uvcvideo") == 0)
        ok = 1;
    if (!ok)
    {
        glog(LOG_WARNING, "modprobe: arguments not permitted");
        fprintf(stderr, "modprobe: arguments not permitted\n");
        return 1;
    }
    glog(LOG_INFO, "modprobe %s%s%s", argv[2], argc == 4 ? " " : "", argc == 4 ? argv[3] : "");
    char *a[4];
    int j = 0;
    a[j++] = "modprobe";
    a[j++] = argv[2];
    if (argc == 4)
        a[j++] = argv[3];
    a[j] = NULL;
    return exec_tool("modprobe", a);
}

/* ---------- nvml clock offsets ---------- */

/* Permissive sanity bounds; NVML rejects values outside the device range. */
#define CORE_MIN -3000
#define CORE_MAX 3000
#define MEM_MIN -10000
#define MEM_MAX 10000

/* Clock types (nvmlClockType_t) and pstate used by the modern offset API. */
#define NVML_CLOCK_GRAPHICS 0
#define NVML_CLOCK_MEM 2
#define NVML_PSTATE_0 0
#define NVML_ERROR_NOT_SUPPORTED 3

/*
 * Modern per-pstate clock offset struct (NVML / driver 555+). LACT uses this
 * API (nvmlDeviceGet/SetClockOffsets) instead of the legacy global VF-curve
 * setters: it reports realistic per-pstate min/max bounds, so the UI sliders
 * cannot reach the absurd values (e.g. +6000 mem) that the legacy
 * MemClkVfOffset range exposed and which hard-crash the GPU (Xid 62 ->
 * NVML_ERROR_RESET_REQUIRED).
 */
typedef struct
{
    unsigned int version;
    unsigned int type;   /* nvmlClockType_t */
    unsigned int pstate; /* nvmlPstates_t */
    int clockOffsetMHz;
    int minClockOffsetMHz;
    int maxClockOffsetMHz;
} nvmlClockOffset_v1_t;

#define NVML_CLOCKOFFSET_V1 ((unsigned int)(sizeof(nvmlClockOffset_v1_t) | (1u << 24)))

typedef int (*nvmlInit_fn)(void);
typedef int (*nvmlGetHandle_fn)(unsigned int index, void **dev);
typedef int (*nvmlSetOffset_fn)(void *dev, int offset);
typedef int (*nvmlShutdown_fn)(void);
typedef int (*nvmlClockOffsets_fn)(void *dev, nvmlClockOffset_v1_t *info);

/*
 * Apply one clock-type offset at P0. Prefer the modern per-pstate API; fall
 * back to the legacy global VF-curve setter only when the modern symbol is
 * absent or returns NVML_ERROR_NOT_SUPPORTED (driver < 555). Returns the NVML
 * return code (0 = success).
 */
static int set_clock_offset(void *dev, unsigned int clockType, int value,
                            nvmlClockOffsets_fn setModern, nvmlSetOffset_fn setLegacy)
{
    if (setModern)
    {
        nvmlClockOffset_v1_t info;
        memset(&info, 0, sizeof(info));
        info.version = NVML_CLOCKOFFSET_V1;
        info.type = clockType;
        info.pstate = NVML_PSTATE_0;
        info.clockOffsetMHz = value;
        int rc = setModern(dev, &info);
        if (rc != NVML_ERROR_NOT_SUPPORTED)
            return rc;
    }
    if (setLegacy)
        return setLegacy(dev, value);
    return NVML_ERROR_NOT_SUPPORTED;
}

int do_nvml(int argc, char **argv)
{
    if (argc != 4)
    {
        fprintf(stderr, "usage: nvml-clocks <core_mhz> <mem_mhz>\n");
        return 1;
    }

    char *endp;
    long core = strtol(argv[2], &endp, 10);
    if (*endp != '\0')
    {
        fprintf(stderr, "core: not an integer\n");
        return 1;
    }
    long mem = strtol(argv[3], &endp, 10);
    if (*endp != '\0')
    {
        fprintf(stderr, "mem: not an integer\n");
        return 1;
    }

    if (core < CORE_MIN || core > CORE_MAX)
    {
        fprintf(stderr, "core offset %ld out of range [%d, %d]\n", core, CORE_MIN, CORE_MAX);
        return 1;
    }
    if (mem < MEM_MIN || mem > MEM_MAX)
    {
        fprintf(stderr, "mem offset %ld out of range [%d, %d]\n", mem, MEM_MIN, MEM_MAX);
        return 1;
    }

    void *lib = dlopen("libnvidia-ml.so.1", RTLD_NOW);
    if (!lib)
    {
        glog(LOG_ERR, "nvml-clocks: dlopen libnvidia-ml.so.1 failed: %s", dlerror());
        fprintf(stderr, "dlopen libnvidia-ml.so.1 failed: %s\n", dlerror());
        return 2;
    }

    nvmlInit_fn nvmlInit = (nvmlInit_fn)dlsym(lib, "nvmlInit_v2");
    nvmlGetHandle_fn nvmlGetHandle = (nvmlGetHandle_fn)dlsym(lib, "nvmlDeviceGetHandleByIndex_v2");
    nvmlClockOffsets_fn nvmlSetModern = (nvmlClockOffsets_fn)dlsym(lib, "nvmlDeviceSetClockOffsets");
    nvmlSetOffset_fn nvmlSetGpc = (nvmlSetOffset_fn)dlsym(lib, "nvmlDeviceSetGpcClkVfOffset");
    nvmlSetOffset_fn nvmlSetMem = (nvmlSetOffset_fn)dlsym(lib, "nvmlDeviceSetMemClkVfOffset");
    nvmlShutdown_fn nvmlShutdown = (nvmlShutdown_fn)dlsym(lib, "nvmlShutdown");

    if (!nvmlInit || !nvmlGetHandle || !nvmlShutdown || (!nvmlSetModern && (!nvmlSetGpc || !nvmlSetMem)))
    {
        glog(LOG_ERR, "nvml-clocks: dlsym failed (driver too old? need NVML 11.x+)");
        fprintf(stderr, "dlsym failed (driver too old? need NVML 11.x+)\n");
        dlclose(lib);
        return 2;
    }

    int rc = nvmlInit();
    if (rc != 0)
    {
        glog(LOG_ERR, "nvml-clocks: nvmlInit_v2 failed: %d", rc);
        fprintf(stderr, "nvmlInit_v2 failed: %d\n", rc);
        dlclose(lib);
        return 2;
    }

    void *dev = NULL;
    rc = nvmlGetHandle(0, &dev);
    if (rc != 0 || dev == NULL)
    {
        glog(LOG_ERR, "nvml-clocks: GetHandleByIndex(0) failed: %d", rc);
        fprintf(stderr, "nvmlDeviceGetHandleByIndex_v2(0) failed: %d\n", rc);
        nvmlShutdown();
        dlclose(lib);
        return 3;
    }

    rc = set_clock_offset(dev, NVML_CLOCK_GRAPHICS, (int)core, nvmlSetModern, nvmlSetGpc);
    if (rc != 0)
    {
        glog(LOG_ERR, "nvml-clocks: set core offset %ld failed: %d", core, rc);
        fprintf(stderr, "set core offset %ld failed\nnvml-error=%d\n", core, rc);
        nvmlShutdown();
        dlclose(lib);
        return 4;
    }
    rc = set_clock_offset(dev, NVML_CLOCK_MEM, (int)mem, nvmlSetModern, nvmlSetMem);
    if (rc != 0)
    {
        glog(LOG_ERR, "nvml-clocks: set mem offset %ld failed: %d", mem, rc);
        fprintf(stderr, "set mem offset %ld failed\nnvml-error=%d\n", mem, rc);
        nvmlShutdown();
        dlclose(lib);
        return 4;
    }

    nvmlShutdown();
    dlclose(lib);
    glog(LOG_INFO, "nvml-clocks: applied core=%ld mem=%ld (%s)", core, mem,
         nvmlSetModern ? "modern" : "legacy");
    printf("applied core=%ld mem=%ld\n", core, mem);
    return 0;
}

/* ---------- nvml live telemetry (temp/util/clocks) ---------- */

typedef int (*nvmlGetTemp_fn)(void *dev, unsigned int sensor, unsigned int *temp);
typedef int (*nvmlGetUtil_fn)(void *dev, void *util);
typedef int (*nvmlGetClock_fn)(void *dev, unsigned int type, unsigned int *clock);
typedef int (*nvmlGetMem_fn)(void *dev, void *mem);
typedef int (*nvmlGetPstate_fn)(void *dev, int *pstate);
typedef int (*nvmlGetPowerUsage_fn)(void *dev, unsigned int *mW);
typedef int (*nvmlGetThrottleReasons_fn)(void *dev, unsigned long long *reasons);
typedef int (*nvmlGetMaxClock_fn)(void *dev, unsigned int type, unsigned int *clock);

/* Live GPU telemetry in a single NVML round-trip (~2-5 ms). Output:
 *   temp=N util=N clock=N mem-clock=N vram-used=N vram-total=N pstate=N
 *   power=N max-clock=N throttle=0xN
 * Keys are omitted when the query returns NOT_SUPPORTED (Pascal: no power). */
int do_nvml_temp(void)
{
    void *lib = dlopen("libnvidia-ml.so.1", RTLD_NOW);
    if (!lib)
    {
        fprintf(stderr, "dlopen libnvidia-ml.so.1 failed: %s\n", dlerror());
        return 2;
    }

    nvmlInit_fn nvmlInit = (nvmlInit_fn)dlsym(lib, "nvmlInit_v2");
    nvmlGetHandle_fn nvmlGetHandle = (nvmlGetHandle_fn)dlsym(lib, "nvmlDeviceGetHandleByIndex_v2");
    nvmlShutdown_fn nvmlShutdown = (nvmlShutdown_fn)dlsym(lib, "nvmlShutdown");
    nvmlGetTemp_fn getTemp = (nvmlGetTemp_fn)dlsym(lib, "nvmlDeviceGetTemperature");
    nvmlGetUtil_fn getUtil = (nvmlGetUtil_fn)dlsym(lib, "nvmlDeviceGetUtilizationRates");
    nvmlGetClock_fn getClock = (nvmlGetClock_fn)dlsym(lib, "nvmlDeviceGetClockInfo");
    nvmlGetMem_fn getMem = (nvmlGetMem_fn)dlsym(lib, "nvmlDeviceGetMemoryInfo");
    nvmlGetPstate_fn getPstate = (nvmlGetPstate_fn)dlsym(lib, "nvmlDeviceGetPerformanceState");
    nvmlGetPowerUsage_fn getPower = (nvmlGetPowerUsage_fn)dlsym(lib, "nvmlDeviceGetPowerUsage");
    nvmlGetThrottleReasons_fn getThrottle = (nvmlGetThrottleReasons_fn)dlsym(lib, "nvmlDeviceGetCurrentClocksEventReasons");
    nvmlGetMaxClock_fn getMaxClock = (nvmlGetMaxClock_fn)dlsym(lib, "nvmlDeviceGetMaxClockInfo");

    if (!nvmlInit || !nvmlGetHandle || !nvmlShutdown)
    {
        fprintf(stderr, "dlsym failed (driver too old?)\n");
        dlclose(lib);
        return 2;
    }

    int rc = nvmlInit();
    if (rc != 0)
    {
        fprintf(stderr, "nvmlInit_v2 failed: %d\n", rc);
        dlclose(lib);
        return 2;
    }

    void *dev = NULL;
    rc = nvmlGetHandle(0, &dev);
    if (rc != 0 || dev == NULL)
    {
        fprintf(stderr, "nvmlDeviceGetHandleByIndex_v2(0) failed: %d\n", rc);
        nvmlShutdown();
        dlclose(lib);
        return 3;
    }

    int printed = 0;
    unsigned int v;

    if (getTemp && getTemp(dev, 0, &v) == 0)
    {
        printf("temp=%u", v);
        printed++;
    }

    if (getUtil)
    {
        struct
        {
            unsigned int gpu;
            unsigned int mem;
        } util;
        if (getUtil(dev, &util) == 0)
        {
            printf("%sutil=%u", printed ? " " : "", util.gpu);
            printed++;
        }
    }

    if (getClock)
    {
        if (getClock(dev, NVML_CLOCK_GRAPHICS, &v) == 0)
        {
            printf("%sclock=%u", printed ? " " : "", v);
            printed++;
        }
        if (getClock(dev, NVML_CLOCK_MEM, &v) == 0)
        {
            printf("%smem-clock=%u", printed ? " " : "", v);
            printed++;
        }
    }

    if (getMem)
    {
        struct
        {
            unsigned long long total;
            unsigned long long free;
            unsigned long long used;
        } mem;
        if (getMem(dev, &mem) == 0)
        {
            printf("%svram-used=%llu vram-total=%llu", printed ? " " : "",
                   mem.used / (1024ULL * 1024ULL), mem.total / (1024ULL * 1024ULL));
            printed++;
        }
    }

    if (getPstate)
    {
        int ps;
        if (getPstate(dev, &ps) == 0)
        {
            printf("%spstate=%d", printed ? " " : "", ps);
            printed++;
        }
    }

    if (getPower)
    {
        unsigned int mW;
        if (getPower(dev, &mW) == 0 && mW <= 200000)
        {
            printf("%spower=%u", printed ? " " : "", mW);
            printed++;
        }
    }

    if (getMaxClock && getMaxClock(dev, NVML_CLOCK_GRAPHICS, &v) == 0)
    {
        printf("%smax-clock=%u", printed ? " " : "", v);
        printed++;
    }

    if (getThrottle)
    {
        unsigned long long reasons;
        if (getThrottle(dev, &reasons) == 0)
        {
            printf("%sthrottle=0x%llx", printed ? " " : "", reasons);
            printed++;
        }
    }

    if (printed)
        printf("\n");

    nvmlShutdown();
    dlclose(lib);
    glog(LOG_INFO, "nvml-temp: queried");
    return 0;
}

/* ---------- nvml read-only info ---------- */

typedef int (*nvmlGetOffset_fn)(void *dev, int *offset);
typedef int (*nvmlGetMinMax_fn)(void *dev, int *min, int *max);
typedef int (*nvmlGetDriver_fn)(char *version, unsigned int length);
typedef int (*nvmlGetSuppMemClocks_fn)(void *dev, unsigned int *count, unsigned int *clocksMHz);
typedef int (*nvmlGetSuppGfxClocks_fn)(void *dev, unsigned int memClockMHz, unsigned int *count, unsigned int *clocksMHz);
typedef int (*nvmlGetPowerMgmtMode_fn)(void *dev, int *mode);

#define NVML_ERROR_INSUFFICIENT_SIZE 7

int do_nvml_info(void)
{
    void *lib = dlopen("libnvidia-ml.so.1", RTLD_NOW);
    if (!lib)
    {
        glog(LOG_ERR, "nvml-info: dlopen libnvidia-ml.so.1 failed: %s", dlerror());
        fprintf(stderr, "dlopen libnvidia-ml.so.1 failed: %s\n", dlerror());
        return 2;
    }

    nvmlInit_fn nvmlInit = (nvmlInit_fn)dlsym(lib, "nvmlInit_v2");
    nvmlGetHandle_fn nvmlGetHandle = (nvmlGetHandle_fn)dlsym(lib, "nvmlDeviceGetHandleByIndex_v2");
    nvmlShutdown_fn nvmlShutdown = (nvmlShutdown_fn)dlsym(lib, "nvmlShutdown");
    nvmlGetDriver_fn getDriver = (nvmlGetDriver_fn)dlsym(lib, "nvmlSystemGetDriverVersion");
    nvmlClockOffsets_fn getModern = (nvmlClockOffsets_fn)dlsym(lib, "nvmlDeviceGetClockOffsets");
    nvmlGetOffset_fn getGpc = (nvmlGetOffset_fn)dlsym(lib, "nvmlDeviceGetGpcClkVfOffset");
    nvmlGetOffset_fn getMem = (nvmlGetOffset_fn)dlsym(lib, "nvmlDeviceGetMemClkVfOffset");
    nvmlGetMinMax_fn getGpcMM = (nvmlGetMinMax_fn)dlsym(lib, "nvmlDeviceGetGpcClkMinMaxVfOffset");
    nvmlGetMinMax_fn getMemMM = (nvmlGetMinMax_fn)dlsym(lib, "nvmlDeviceGetMemClkMinMaxVfOffset");
    nvmlGetSuppMemClocks_fn getSuppMem = (nvmlGetSuppMemClocks_fn)dlsym(lib, "nvmlDeviceGetSupportedMemoryClocks");
    nvmlGetSuppGfxClocks_fn getSuppGfx = (nvmlGetSuppGfxClocks_fn)dlsym(lib, "nvmlDeviceGetSupportedGraphicsClocks");
    nvmlGetPowerMgmtMode_fn getPwrMode = (nvmlGetPowerMgmtMode_fn)dlsym(lib, "nvmlDeviceGetPowerManagementMode");

    if (!nvmlInit || !nvmlGetHandle || !nvmlShutdown)
    {
        glog(LOG_ERR, "nvml-info: dlsym failed (driver too old?)");
        fprintf(stderr, "dlsym failed (driver too old?)\n");
        dlclose(lib);
        return 2;
    }

    int rc = nvmlInit();
    if (rc != 0)
    {
        glog(LOG_ERR, "nvml-info: nvmlInit_v2 failed: %d", rc);
        fprintf(stderr, "nvmlInit_v2 failed: %d\n", rc);
        dlclose(lib);
        return 2;
    }

    void *dev = NULL;
    rc = nvmlGetHandle(0, &dev);
    if (rc != 0 || dev == NULL)
    {
        glog(LOG_ERR, "nvml-info: GetHandleByIndex(0) failed: %d", rc);
        fprintf(stderr, "nvmlDeviceGetHandleByIndex_v2(0) failed: %d\n", rc);
        nvmlShutdown();
        dlclose(lib);
        return 3;
    }

    if (getDriver)
    {
        char ver[96];
        if (getDriver(ver, sizeof(ver)) == 0)
            printf("driver=%s\n", ver);
    }
    int v, mn, mx;
    int gotCore = 0, gotMem = 0;
    if (getModern)
    {
        nvmlClockOffset_v1_t info;
        memset(&info, 0, sizeof(info));
        info.version = NVML_CLOCKOFFSET_V1;
        info.type = NVML_CLOCK_GRAPHICS;
        info.pstate = NVML_PSTATE_0;
        if (getModern(dev, &info) == 0)
        {
            printf("core-offset=%d\n", info.clockOffsetMHz);
            printf("core-range=%d,%d\n", info.minClockOffsetMHz, info.maxClockOffsetMHz);
            gotCore = 1;
        }
        memset(&info, 0, sizeof(info));
        info.version = NVML_CLOCKOFFSET_V1;
        info.type = NVML_CLOCK_MEM;
        info.pstate = NVML_PSTATE_0;
        if (getModern(dev, &info) == 0)
        {
            printf("mem-offset=%d\n", info.clockOffsetMHz);
            printf("mem-range=%d,%d\n", info.minClockOffsetMHz, info.maxClockOffsetMHz);
            gotMem = 1;
        }
    }
    if (!gotCore)
    {
        if (getGpc && getGpc(dev, &v) == 0)
            printf("core-offset=%d\n", v);
        if (getGpcMM && getGpcMM(dev, &mn, &mx) == 0)
            printf("core-range=%d,%d\n", mn, mx);
    }
    if (!gotMem)
    {
        if (getMem && getMem(dev, &v) == 0)
            printf("mem-offset=%d\n", v);
        if (getMemMM && getMemMM(dev, &mn, &mx) == 0)
            printf("mem-range=%d,%d\n", mn, mx);
    }

    /* Capability flags. A clock type is lockable when NVML reports supported
     * clocks for it; power-mgmt indicates the power limit is settable. */
    int lockMem = 0, lockGpu = 0, powerMgmt = 0;
    if (getSuppMem)
    {
        unsigned int cnt = 256;
        unsigned int clk[256];
        int r = getSuppMem(dev, &cnt, clk);
        if ((r == 0 || r == NVML_ERROR_INSUFFICIENT_SIZE) && cnt > 0)
        {
            lockMem = 1;
            if (getSuppGfx)
            {
                unsigned int gcnt = 256;
                unsigned int gclk[256];
                int rg = getSuppGfx(dev, clk[0], &gcnt, gclk);
                if ((rg == 0 || rg == NVML_ERROR_INSUFFICIENT_SIZE) && gcnt > 0)
                    lockGpu = 1;
            }
        }
    }
    if (getPwrMode)
    {
        int mode = 0;
        if (getPwrMode(dev, &mode) == 0 && mode == 1)
            powerMgmt = 1;
    }
    printf("lock-mem=%d\n", lockMem);
    printf("lock-gpu=%d\n", lockGpu);
    printf("power-mgmt=%d\n", powerMgmt);

    /* Static device info: thermal limits, VBIOS, memory bus width. */
    typedef int (*nvmlGetTempThresh_fn)(void *dev, unsigned int thresh, unsigned int *temp);
    typedef int (*nvmlGetVbios_fn)(void *dev, char *ver, unsigned int len);
    typedef int (*nvmlGetMemBusW_fn)(void *dev, unsigned int *width);

    nvmlGetTempThresh_fn getTempThresh = (nvmlGetTempThresh_fn)dlsym(lib, "nvmlDeviceGetTemperatureThreshold");
    nvmlGetVbios_fn getVbios = (nvmlGetVbios_fn)dlsym(lib, "nvmlDeviceGetVbiosVersion");
    nvmlGetMemBusW_fn getMemBusW = (nvmlGetMemBusW_fn)dlsym(lib, "nvmlDeviceGetMemoryBusWidth");

    if (getTempThresh)
    {
        unsigned int tv;
        /* 0 = shutdown, 1 = slowdown */
        if (getTempThresh(dev, 0, &tv) == 0)
            printf("temp-shutdown=%u\n", tv);
        if (getTempThresh(dev, 1, &tv) == 0)
            printf("temp-slowdown=%u\n", tv);
    }
    if (getVbios)
    {
        char vbios[96];
        if (getVbios(dev, vbios, sizeof(vbios)) == 0)
            printf("vbios=%s\n", vbios);
    }
    if (getMemBusW)
    {
        unsigned int w;
        if (getMemBusW(dev, &w) == 0)
            printf("mem-bus-width=%u\n", w);
    }

    nvmlShutdown();
    dlclose(lib);
    glog(LOG_INFO, "nvml-info: queried");
    return 0;
}

/* ---------- nvml running processes ---------- */

/* nvmlProcessInfo layouts. v1 (unsuffixed symbols) lacks the MIG instance
 * fields; v2/v3 share the wider struct. The matching layout is picked by
 * which symbol resolves. */
typedef struct
{
    unsigned int pid;
    unsigned long long usedGpuMemory;
} nvml_proc_v1_t;

typedef struct
{
    unsigned int pid;
    unsigned long long usedGpuMemory;
    unsigned int gpuInstanceId;
    unsigned int computeInstanceId;
} nvml_proc_v2_t;

typedef int (*nvmlGetCount_fn)(unsigned int *count);
typedef int (*nvmlGetProcs_fn)(void *dev, unsigned int *count, void *infos);

#define NVML_PROCS_MAX 512

static void print_nvml_procs(void *dev, nvmlGetProcs_fn getProcs, int wide, const char *kind)
{
    if (!getProcs)
        return;
    unsigned int count = NVML_PROCS_MAX;
    /* Static: wide structs * 512 would be large on the stack. */
    static unsigned char buf[NVML_PROCS_MAX * sizeof(nvml_proc_v2_t)];
    memset(buf, 0, sizeof(buf));
    int rc = getProcs(dev, &count, buf);
    if (rc != 0 && rc != NVML_ERROR_INSUFFICIENT_SIZE)
        return;
    if (count > NVML_PROCS_MAX)
        count = NVML_PROCS_MAX;
    size_t stride = wide ? sizeof(nvml_proc_v2_t) : sizeof(nvml_proc_v1_t);
    for (unsigned int i = 0; i < count; i++)
    {
        unsigned int pid;
        memcpy(&pid, buf + i * stride, sizeof(pid));
        if (pid > 0)
            printf("%u\t%s\n", pid, kind);
    }
}

int do_nvml_procs(void)
{
    void *lib = dlopen("libnvidia-ml.so.1", RTLD_NOW);
    if (!lib)
    {
        fprintf(stderr, "dlopen libnvidia-ml.so.1 failed: %s\n", dlerror());
        return 2;
    }

    nvmlInit_fn nvmlInit = (nvmlInit_fn)dlsym(lib, "nvmlInit_v2");
    nvmlGetCount_fn nvmlGetCount = (nvmlGetCount_fn)dlsym(lib, "nvmlDeviceGetCount_v2");
    nvmlGetHandle_fn nvmlGetHandle = (nvmlGetHandle_fn)dlsym(lib, "nvmlDeviceGetHandleByIndex_v2");
    nvmlShutdown_fn nvmlShutdown = (nvmlShutdown_fn)dlsym(lib, "nvmlShutdown");

    /* Prefer the newest process-list symbols; struct width follows the symbol. */
    int gfxWide = 1, cmpWide = 1, mpsWide = 1;
    nvmlGetProcs_fn getGraphics = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetGraphicsRunningProcesses_v3");
    if (!getGraphics)
        getGraphics = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetGraphicsRunningProcesses_v2");
    if (!getGraphics)
    {
        getGraphics = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetGraphicsRunningProcesses");
        gfxWide = 0;
    }
    nvmlGetProcs_fn getCompute = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetComputeRunningProcesses_v3");
    if (!getCompute)
        getCompute = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetComputeRunningProcesses_v2");
    if (!getCompute)
    {
        getCompute = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetComputeRunningProcesses");
        cmpWide = 0;
    }
    /* MPS clients hold a GPU context via the MPS server; the client pid may
     * not appear in the regular compute list (the server does). */
    nvmlGetProcs_fn getMps = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetMPSComputeRunningProcesses_v3");
    if (!getMps)
        getMps = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetMPSComputeRunningProcesses_v2");
    if (!getMps)
    {
        getMps = (nvmlGetProcs_fn)dlsym(lib, "nvmlDeviceGetMPSComputeRunningProcesses");
        mpsWide = 0;
    }

    if (!nvmlInit || !nvmlGetCount || !nvmlGetHandle || !nvmlShutdown || (!getGraphics && !getCompute))
    {
        fprintf(stderr, "dlsym failed (NVML too old?)\n");
        dlclose(lib);
        return 2;
    }

    int rc = nvmlInit();
    if (rc != 0)
    {
        fprintf(stderr, "nvmlInit_v2 failed: %d\n", rc);
        dlclose(lib);
        return 2;
    }

    unsigned int devCount = 0;
    if (nvmlGetCount(&devCount) != 0)
        devCount = 0;
    for (unsigned int i = 0; i < devCount; i++)
    {
        void *dev = NULL;
        if (nvmlGetHandle(i, &dev) != 0 || dev == NULL)
            continue;
        print_nvml_procs(dev, getGraphics, gfxWide, "graphics");
        print_nvml_procs(dev, getCompute, cmpWide, "compute");
        print_nvml_procs(dev, getMps, mpsWide, "mps");
    }

    nvmlShutdown();
    dlclose(lib);
    return 0;
}

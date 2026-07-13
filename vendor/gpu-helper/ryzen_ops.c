/* Ryzen SMU operations — thin adapter over the vendored library.
 * Provides info/set/probe subcommands for gpu-helper. */

#include "ryzen_ops.h"
#include "ryzen/ryzenadj.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Map parameter names to set/get function pointers. */

typedef int (*set_fn)(ryzen_access, uint32_t);
typedef float (*get_fn)(ryzen_access);

typedef struct
{
    const char *name;
    set_fn set;
    get_fn get_limit; /* current limit (NULL if none) */
    get_fn get_value; /* current actual value (NULL if none) */
} ryzen_param;

static const ryzen_param params[] = {
    /* Power limits (mW) */
    {"stapm-limit", set_stapm_limit, get_stapm_limit, get_stapm_value},
    {"fast-limit", set_fast_limit, get_fast_limit, get_fast_value},
    {"slow-limit", set_slow_limit, get_slow_limit, get_slow_value},
    {"apu-slow-limit", set_apu_slow_limit, get_apu_slow_limit, get_apu_slow_value},
    /* Time constants (s) */
    {"stapm-time", set_stapm_time, get_stapm_time, NULL},
    {"slow-time", set_slow_time, get_slow_time, NULL},
    /* Temperature (°C) */
    {"tctl-temp", set_tctl_temp, get_tctl_temp, get_tctl_temp_value},
    {"apu-skin-temp", set_apu_skin_temp_limit, get_apu_skin_temp_limit, get_apu_skin_temp_value},
    {"dgpu-skin-temp", set_dgpu_skin_temp_limit, get_dgpu_skin_temp_limit, get_dgpu_skin_temp_value},
    /* Current limits (mA) */
    {"vrm-current", set_vrm_current, get_vrm_current, get_vrm_current_value},
    {"vrmsoc-current", set_vrmsoc_current, get_vrmsoc_current, get_vrmsoc_current_value},
    {"vrmmax-current", set_vrmmax_current, get_vrmmax_current, get_vrmmax_current_value},
    {"vrmsocmax-current", set_vrmsocmax_current, get_vrmsocmax_current, get_vrmsocmax_current_value},
    /* iGPU clocks (MHz) */
    {"max-gfxclk", set_max_gfxclk_freq, NULL, NULL},
    {"min-gfxclk", set_min_gfxclk_freq, NULL, NULL},
    /* Misc */
    {"skin-temp-limit", set_skin_temp_power_limit, NULL, NULL},
    {"prochot-ramp", set_prochot_deassertion_ramp, NULL, NULL},
    /* Curve optimizer */
    {"coall", set_coall, NULL, NULL},
    {"coper", set_coper, NULL, NULL},
    {"cogfx", set_cogfx, NULL, NULL},
    {NULL, NULL, NULL, NULL}};

/* No-arg set functions (toggles). */
typedef int (*set_noarg_fn)(ryzen_access);

typedef struct
{
    const char *name;
    set_noarg_fn set;
} ryzen_toggle;

static const ryzen_toggle toggles[] = {
    {"power-saving", set_power_saving},
    {"max-performance", set_max_performance},
    {"enable-oc", set_enable_oc},
    {"disable-oc", set_disable_oc},
    {NULL, NULL}};

static const char *family_str(enum ryzen_family f)
{
    switch (f)
    {
    case FAM_RAVEN:
        return "Raven";
    case FAM_PICASSO:
        return "Picasso";
    case FAM_RENOIR:
        return "Renoir";
    case FAM_CEZANNE:
        return "Cezanne";
    case FAM_DALI:
        return "Dali";
    case FAM_LUCIENNE:
        return "Lucienne";
    case FAM_VANGOGH:
        return "Vangogh";
    case FAM_REMBRANDT:
        return "Rembrandt";
    case FAM_MENDOCINO:
        return "Mendocino";
    case FAM_PHOENIX:
        return "Phoenix";
    case FAM_HAWKPOINT:
        return "HawkPoint";
    case FAM_DRAGONRANGE:
        return "DragonRange";
    case FAM_KRACKANPOINT:
        return "KrackanPoint";
    case FAM_STRIXPOINT:
        return "StrixPoint";
    case FAM_STRIXHALO:
        return "StrixHalo";
    case FAM_FIRERANGE:
        return "FireRange";
    default:
        return "Unknown";
    }
}

static int err_to_exit(int e)
{
    switch (e)
    {
    case 0:
        return 0;
    case ADJ_ERR_FAM_UNSUPPORTED:
        return 1;
    case ADJ_ERR_SMU_UNSUPPORTED:
        return 2;
    case ADJ_ERR_SMU_REJECTED:
        return 3;
    default:
        return 4;
    }
}

int ryzen_do_info(void)
{
    ryzen_access ry = init_ryzenadj();
    if (!ry)
        return -1;

    printf("family=%s\n", family_str(get_cpu_family(ry)));
    printf("bios-if=%d\n", get_bios_if_ver(ry));

    if (init_table(ry) == 0 && refresh_table(ry) == 0)
    {
        for (const ryzen_param *p = params; p->name; p++)
        {
            if (p->get_limit)
            {
                float v = p->get_limit(ry);
                if (v > 0)
                    printf("%s=%.0f\n", p->name, v);
            }
            if (p->get_value)
            {
                float v = p->get_value(ry);
                if (v > 0)
                    printf("%s-value=%.1f\n", p->name, v);
            }
        }
        /* Extra read-only metrics */
        float v;
        if ((v = get_socket_power(ry)) > 0)
            printf("socket-power=%.1f\n", v);
        if ((v = get_gfx_clk(ry)) > 0)
            printf("gfx-clk=%.0f\n", v);
        if ((v = get_gfx_temp(ry)) > 0)
            printf("gfx-temp=%.1f\n", v);
        if ((v = get_gfx_volt(ry)) > 0)
            printf("gfx-volt=%.3f\n", v);
        if ((v = get_mem_clk(ry)) > 0)
            printf("mem-clk=%.0f\n", v);
        if ((v = get_fclk(ry)) > 0)
            printf("fclk=%.0f\n", v);
        if ((v = get_soc_power(ry)) > 0)
            printf("soc-power=%.1f\n", v);
        if ((v = get_soc_volt(ry)) > 0)
            printf("soc-volt=%.3f\n", v);
        if ((v = get_cclk_setpoint(ry)) > 0)
            printf("cclk-setpoint=%.0f\n", v);
        if ((v = get_cclk_busy_value(ry)) > 0)
            printf("cclk-busy=%.1f\n", v);
        if ((v = get_l3_clk(ry)) > 0)
            printf("l3-clk=%.0f\n", v);
        if ((v = get_l3_temp(ry)) > 0)
            printf("l3-temp=%.1f\n", v);
    }

    cleanup_ryzenadj(ry);
    return 0;
}

int ryzen_do_set(const char *param, unsigned int value)
{
    /* Check toggles first (no value argument). */
    for (const ryzen_toggle *t = toggles; t->name; t++)
    {
        if (strcmp(param, t->name) == 0)
        {
            ryzen_access ry = init_ryzenadj();
            if (!ry)
                return -1;
            int rc = t->set(ry);
            cleanup_ryzenadj(ry);
            return err_to_exit(rc);
        }
    }

    /* Regular value-based params. */
    for (const ryzen_param *p = params; p->name; p++)
    {
        if (strcmp(param, p->name) == 0)
        {
            ryzen_access ry = init_ryzenadj();
            if (!ry)
                return -1;
            int rc = p->set(ry, value);
            cleanup_ryzenadj(ry);
            return err_to_exit(rc);
        }
    }

    fprintf(stderr, "ryzen-set: unknown parameter '%s'\n", param);
    return -1;
}

int ryzen_do_probe(void)
{
    ryzen_access ry = init_ryzenadj();
    if (!ry)
        return -1;

    /* Dry-run: set_* report family support without touching the SMU. */
    set_probe_dry_run(1);

    printf("family=%s\n", family_str(get_cpu_family(ry)));
    printf("supported=");

    int first = 1;
    for (const ryzen_param *p = params; p->name; p++)
    {
        if (p->set(ry, 0) != ADJ_ERR_FAM_UNSUPPORTED)
        {
            if (!first)
                printf(",");
            printf("%s", p->name);
            first = 0;
        }
    }
    for (const ryzen_toggle *t = toggles; t->name; t++)
    {
        if (t->set(ry) != ADJ_ERR_FAM_UNSUPPORTED)
        {
            if (!first)
                printf(",");
            printf("%s", t->name);
            first = 0;
        }
    }
    printf("\n");

    set_probe_dry_run(0);
    cleanup_ryzenadj(ry);
    return 0;
}

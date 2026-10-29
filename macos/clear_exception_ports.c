// SPDX-License-Identifier: GPL-3.0-or-later (see NOTICE.md).
// clear-exception-ports COMMAND [ARGUMENT...]
//
// Runs COMMAND without the Mach exception ports this process inherited.
// The Epic Games Launcher starts run_regeneratefm.sh itself, so FM26 inherits
// Epic's crash handler. On BepInEx's first start under Rosetta 2, funchook's
// code patching then waits for an exception reply that never comes and FM26
// hangs before its window opens. Started through this, FM26 has no inherited
// handlers, as when Steam starts it. macos/build.py builds it for arm64 and
// x86_64.

#include <mach/mach.h>
#include <stdio.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: clear-exception-ports COMMAND [ARGUMENT...]\n");
        return 2;
    }
    kern_return_t result = task_set_exception_ports(mach_task_self(), EXC_MASK_ALL, MACH_PORT_NULL,
                                                    EXCEPTION_DEFAULT, THREAD_STATE_NONE);
    if (result != KERN_SUCCESS)
        fprintf(stderr, "clear-exception-ports: task_set_exception_ports failed (%d); starting anyway.\n", result);
    execv(argv[1], argv + 1);
    perror("clear-exception-ports");
    return 127;
}

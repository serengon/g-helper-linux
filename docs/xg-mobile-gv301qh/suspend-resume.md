# Suspensión, reanudación y arranque en frío con XG Mobile

**Estado:** fallo reproducido; workaround operativo confirmado; resume aislado al
camino PCI/NVIDIA previo al retorno a userspace; causa final pendiente

**Fechas de las pruebas:** 2026-08-19 y 2026-08-20

**Equipo:** ASUS ROG Flow X13 GV301QH, BIOS 418, XG Mobile RTX 3080

**Software:** Fedora 44, kernel 7.1.8-200.fc44.x86_64, NVIDIA 610.57.04
open kernel module, GNOME 50.4 sobre Wayland

Este documento separa dos fallos que se parecen desde afuera, pero ocurren en
momentos diferentes:

1. La máquina no vuelve de `s2idle` cuando la XG está activa.
2. NVIDIA se bloquea durante un arranque en frío si la XG quedó activa.

No deben tratarse como un solo bug hasta tener evidencia que lo demuestre.

## Estado funcional previo

La máquina arrancó sin la XG, con la GTX 1650 y esta línea efectiva:

```text
rd.driver.blacklist=nouveau,nova_core modprobe.blacklist=nouveau,nova_core pcie_port_pm=off
```

Se verificó:

```text
egpu_connected=0
egpu_enable=0
GTX 1650: driver nvidia
tareas D: 0
holders /dev/nvidia*: 0
ghelperd: activo
GHelper: activo
```

Después se conectó físicamente la XG. El preflight continuó limpio:

```text
egpu_connected=1
egpu_enable=0
GPU visible: GTX 1650 10de:1f9d
tareas D: 0
holders /dev/nvidia*: 0
```

La activación desde GHelper comenzó a las `23:19:33` y terminó a las
`23:19:54`:

```text
ghelperd: Starting live XG Mobile transition: False -> True.
ghelperd: XG Mobile HID: ... 0B05:1970 ... hidraw9
ghelperd: XG Mobile live transition completed: enabled=True.
```

El resultado fue correcto:

```text
egpu_connected=1
egpu_enable=1
0000:01:00.0 10de:249c RTX 3080 Laptop GPU
driver: nvidia
nvidia-smi: NVIDIA GeForce RTX 3080 Laptop GPU
GTX 1650: ausente
tareas D: 0
```

Esto vuelve a confirmar que la transición en caliente funciona con
`pcie_port_pm=off`.

### El parámetro global no es necesario para el hot-switch

Una prueba posterior arrancó sin `pcie_port_pm=off` y aplicó únicamente:

```text
/sys/bus/pci/devices/0000:00:01.1/power/control=on
```

`0000:00:01.1` es el root port AMD `1022:1633`, subsistema ASUS `1043:1662`,
que contiene el endpoint XG. Con esa única excepción de runtime PM se completaron
activación, desactivación y reactivación en caliente. Por lo tanto:

- `pcie_port_pm=off` sigue siendo el workaround instalado y conocido;
- no es la causa del bloqueo al reanudar;
- para el hot-switch puede reemplazarse por una política acotada al root port;
- la política acotada todavía debe integrarse y probarse en el MVP antes de
  retirar el parámetro global.

## Fallo 1: la XG activa no vuelve de `s2idle`

### Reproducción

Con la RTX 3080 activa y ociosa en P8:

1. Se cerró la tapa.
2. Wi-Fi y SSH desaparecieron inmediatamente, como se espera al suspender.
3. Se abrió la tapa aproximadamente 15 segundos después.
4. No volvió el panel, la red ni SSH.
5. Un toque corto al botón de encendido tampoco produjo respuesta.
6. Fue necesario mantener presionado el botón para cortar la energía.

### Último journal persistido

El arranque fallido termina así:

```text
23:20:24 systemd-logind: Suspending...
23:20:25 systemd: Starting nvidia-suspend.service...
23:20:25 systemd: nvidia-suspend.service: Skipped due to 'exec-condition'.
23:20:25 systemd-sleep: Performing sleep operation 'suspend'...
23:20:25 kernel: PM: suspend entry (s2idle)
23:20:25 kernel: Filesystems sync: 0.012 seconds
23:20:25 kernel: rfkill: input handler enabled
```

No existe después ninguna línea `PM: suspend exit`, `resume from suspend-to-idle`
ni error de reanudación. El kernel entró al camino de suspensión y nunca volvió a
un punto desde el cual pudiera escribir al journal.

### Por qué el servicio NVIDIA fue omitido

El servicio instalado tiene esta condición:

```text
grep -qs 'UseKernelSuspendNotifiers: 0' /proc/driver/nvidia/params
```

El módulo activo informó:

```text
PreserveVideoMemoryAllocations: 2
UseKernelSuspendNotifiers: 1
EnableS0ixPowerManagement: 0
TemporaryFilePath: ""
```

Por lo tanto, que `nvidia-suspend.service` se omita es coherente con la
configuración: el open kernel module eligió los callbacks de suspensión del
kernel. Según la documentación de NVIDIA, con módulos abiertos y
`UseKernelSuspendNotifiers=1` la preservación se maneja automáticamente. La
omisión del servicio no demuestra una mala configuración.

### A/B de momento de resume NVIDIA: negativo

El 2026-08-20 se probó el camino alternativo soportado por el paquete Fedora:

```text
NVreg_EnableS0ixPowerManagement=1
NVreg_UseKernelSuspendNotifiers=0
PreserveVideoMemoryAllocations=2
```

Los servicios `nvidia-suspend.service` y `nvidia-resume.service` estaban
habilitados. Con la RTX activa y sana, el journal persistió:

```text
Lid closed.
Starting nvidia-suspend.service...
nvidia-suspend.service
Finished nvidia-suspend.service.
Starting systemd-suspend.service...
PM: suspend entry (s2idle)
```

`nvidia-suspend.service` terminó correctamente en 1,674 segundos. No aparece
`PM: suspend exit` ni el inicio de `nvidia-resume.service`: el kernel nunca llegó
a devolver control a systemd. Mover el resume completo desde el notificador
`PM_POST_SUSPEND` al servicio de userspace no resuelve el bloqueo.

### `pm_trace`: huella RTC obsoleta; no prueba contra el audio XG

Se repitió el fallo con:

```text
/sys/power/pm_trace=1
/sys/power/pm_async=0
/sys/power/pm_print_times=1
/sys/power/pm_debug_messages=1
```

Después del corte forzado, la XG se desconectó con la laptop apagada y el arranque
de recuperación informó:

```text
PM:   Magic number: 5:373:726
```

No hubo coincidencia automática porque el endpoint XG ya no estaba enumerado.
Aplicando el mismo hash SDBM del kernel, semilla `7919` y módulo `1009`:

```text
0000:01:00.0  RTX 3080 video   -> 725
0000:01:00.1  XG HDMI audio    -> 726
0000:00:01.1  root port AMD    -> 509
```

La coincidencia parecía apuntar a `0000:01:00.1`, la función de audio HDMI de la
XG controlada por `snd_hda_intel`, pero dos A/B posteriores la refutaron:

1. Desvincular únicamente `0000:01:00.1` de `snd_hda_intel` no evitó el bloqueo.
2. Retirar por completo `0000:01:00.1` del árbol PCI tampoco evitó el bloqueo.

Después de ambos cortes el firmware siguió informando exactamente
`5:373:726`, incluso cuando el dispositivo cuyo hash sería `726` no existía. La
huella quedó escrita en el RTC por el primer ensayo y los intentos posteriores
se bloquearon antes de que `pm_trace` pudiera reemplazarla. No debe usarse como
evidencia de causalidad contra el audio.

### `pm_test`: el corte está entre `devices` y la transición de plataforma

Con XG activa, RTX 3080 vinculada a NVIDIA 610 y audio XG presente se ejecutaron
dos suspensiones simuladas:

```text
pm_test=devices:
PM: suspend devices took 0.951 seconds
PM: suspend debug: Waiting for 5 second(s).
PM: resume devices took 5.398 seconds
PM: suspend exit
```

El nivel `devices` volvió limpio, con la RTX sana y sin tareas `D`.

```text
pm_test=platform:
PM: suspend devices took 1.677 seconds
ACPI: EC: interrupt blocked
PM: suspend debug: Waiting for 5 second(s).
amd_pmc AMDI0005:00: Last suspend didn't reach deepest state
ACPI: EC: interrupt unblocked
NVRM: RmHandleDNotifierEvent: Failed to handle ACPI D-Notifier event, status=0x11
PM: resume devices took 42.342 seconds
WARNING: kernel/power/suspend_test.c:53 at suspend_test_finish
PM: suspend exit
```

El sistema terminó recuperándose, pero excedió por mucho la ventana del test.
En el fuente abierto NVIDIA 610, `0x00000011` corresponde a
`NV_ERR_GPU_NOT_FULL_POWER`: RM recibió trabajo ACPI cuando todavía no consideraba
la GPU en alimentación completa.

Este ensayo se disparó escribiendo directamente `mem` en sysfs. Con
`NVreg_UseKernelSuspendNotifiers=0`, ese camino no ejecuta previamente
`nvidia-suspend.service`, por lo que la demora de 42 segundos y el D-Notifier no
eran una reproducción limpia del flujo configurado.

La repetición correcta mediante `systemctl suspend` ejecutó ambos servicios
NVIDIA y produjo:

```text
nvidia-suspend.service: Finished
PM: suspend devices took 0.107 seconds
PM: suspend debug: Waiting for 5 second(s).
NVRM: RmHandleDNotifierEvent ... status=0x11
PM: resume devices took 0.206 seconds
PM: suspend exit
nvidia-resume.service: Finished
```

El D-Notifier apareció igualmente, pero sin demora ni daño observable en kernel o
RTX. No es causa suficiente del bloqueo y no justifica por sí solo parchear el
driver. El usuario indicó después que la imagen podía no haberse recuperado antes
del siguiente ensayo; el journal muestra GDM/autenticación, reinicios auxiliares
de X11 y `Cursor update failed: drmModeAtomicCommit: Invalid argument`, pero ningún
coredump. El resultado debe clasificarse como **kernel/RTX recuperados, estado
gráfico no validado**, no como PASS integral.

`pm_test=processors` se intentó después, pero s2idle lo rechazó explícitamente:

```text
PM: Unsupported test mode for suspend to idle, please choose none/freezer/devices/platform.
```

Por tanto `platform` es el último nivel simulado aplicable a este equipo.

### Investigación comunitaria: misma firma, causas distintas

La búsqueda por el mensaje exacto encontró NVIDIA open-gpu-kernel-modules #1142,
creado en mayo de 2026 sobre Fedora 44. Ese equipo también quedaba negro, con los
ventiladores activos, y registraba:

```text
RmHandleDNotifierEvent: Failed to handle ACPI D-Notifier event, status=0x11
```

Allí la cadena empezaba antes con un timeout de 30 segundos y un fallo al descargar
GSP. El autor encontró denegaciones SELinux de `systemd_sleep_t` al archivo
temporal usado para preservar VRAM y lo resolvió con una política local. NVIDIA
confirmó esa interacción y explicó la diferencia entre suspend notifiers del
kernel y el flujo de `nvidia-suspend.service`.

Ese arreglo **no se debe copiar** a esta X13: SELinux está en `Enforcing`, pero no
hay AVC de `systemd-sleep`, NVIDIA ni archivos temporales en ninguno de nuestros
intentos. Además:

```text
UseKernelSuspendNotifiers: 0
PreserveVideoMemoryAllocations: 2
TemporaryFilePath: ""
nvidia-powerd.service: inactive
```

Por tanto compartimos la firma tardía `NV_ERR_GPU_NOT_FULL_POWER`, no la causa
demostrada del caso Lenovo. Los reportes públicos de XG Mobile encontrados cubren
enable/disable y cambio de GPU, pero no documentan una solución para suspender con
la XG activa. Hasta encontrar evidencia contraria, el problema específico
GV301QH + XG Mobile debe tratarse como no resuelto públicamente.

La plataforma solo expone:

```text
/sys/power/mem_sleep: [s2idle]
```

No hay `deep`/S3 disponible para seleccionar mediante `mem_sleep_default=deep`.

## Fallo 2: arranque en frío con la XG activa

Después del corte forzado, la máquina arrancó con la XG todavía conectada y
`egpu_enable=1`. El kernel detectó la RTX, pero NVIDIA no terminó de inicializar:

```text
nvidia 0000:01:00.0: enabling device (0000 -> 0003)
NVRM: loading NVIDIA UNIX Open Kernel Module ... 610.57.04
nvidia-modeset: Loading NVIDIA UNIX Open Kernel Mode Setting Driver ...
[drm] [nvidia-drm] [GPU ID 0x00000100] Loading driver
```

No aparece el posterior `Initialized nvidia-drm`. Quedaron bloqueados en estado
`D`:

```text
kworker/4:1+kac
udev-worker
nvidia-powerd
gst-plugin-scanner (dos instancias)
nvidia-smi (dos instancias de diagnóstico)
```

Los procesos que entraron por RM quedaron en:

```text
os_acquire_rwlock_write
```

En reproducciones anteriores del mismo arranque aparecía además:

```text
NVRM: nvAssertFailedNoLog: Assertion failed: !rmapiLockIsOwner() @ rmapi.c:563
```

Una tarea `D` no puede terminarse con una señal normal. Una vez que NVIDIA llega
a este estado, no es seguro seguir ejecutando `nvidia-smi`, abrir aplicaciones
que consulten la GPU ni intentar descargar los módulos. El apagado ordenado puede
quedar esperando indefinidamente y terminar en otro corte forzado.

## Workaround operativo actual

Hasta resolver ambos fallos, la regla es:

> No suspender, reiniciar ni apagar mientras `egpu_enable=1`.

Secuencia segura:

1. Cerrar juegos, CUDA, monitores y cualquier holder de `/dev/nvidia*`.
2. Pulsar **Desactivar** en GHelper.
3. Esperar y verificar `egpu_connected=1`, `egpu_enable=0` y GTX 1650 visible.
4. Recién entonces cerrar la tapa, reiniciar o apagar.
5. Para usar de nuevo la RTX, arrancar con `egpu_enable=0`, conectar la XG en
   caliente si estaba retirada y pulsar **Activar**.

Si la máquina ya está bloqueada:

1. No retirar físicamente la XG mientras la laptop sigue energizada.
2. Forzar el apagado manteniendo el botón.
3. Con la laptop apagada, desconectar físicamente la XG.
4. Arrancar sin ella y recuperar el estado base.

El MVP debe implementar inmediatamente una inhibición de suspensión/reinicio
mientras la XG esté activa, con un mensaje que pida desactivarla. Eso evita pérdida
de datos, pero es un cinturón de seguridad, no la solución técnica final.

## Hipótesis de solución, en orden de prueba

### 1. A/B de kernel 7.1 contra 6.19 — completado, negativo

Se probó NVIDIA 610 sobre `7.1.8-200.fc44.x86_64` y
`6.19.10-300.fc44.x86_64`. Ambos kernels bloquearon el resume con la RTX activa.
No es una regresión exclusiva de kernel 7.x en este equipo.

### 2. Suspender con NVIDIA descargada, sin desactivar el XG — resume confirmado

Se desvincularon video y audio, se descargaron todos los módulos NVIDIA y se
mantuvo `egpu_enable=1`. La laptop suspendió y reanudó conservando la misma sesión
GNOME. Esto confirma que el hardware XG energizado por sí solo no bloquea s2idle.

Al volver, udev cargó NVIDIA antes de que el endpoint estuviera listo. GSP falló:

```text
gpuWaitForGfwBootComplete_TU102
kgspWaitForGfwBootOk
RmInitAdapter failed (0x62:0x55:2119)
```

Descargar y recargar el módulo sin un reset físico/lógico del XG no recuperó la
RTX. Una desactivación/activación completa sí la recuperó. El hook es un workaround
válido si completa el ciclo de energía XG después del resume; un simple
unbind/rebind no alcanza.

### 3. Retirar solo el endpoint PCI antes de suspender

Si descargar módulos no alcanza, se puede probar `remove` sobre las funciones
PCI de video/audio después de liberarlas, sin cambiar `egpu_enable`. Al reanudar,
se hace `rescan` y se carga el driver. Esta variante mantiene el XG energizado pero
evita que el core PCI intente suspender el endpoint removible.

### 4. NVIDIA S0ix — completado, negativo

El driver tiene `NVreg_EnableS0ixPowerManagement=0`, mientras el equipo solo ofrece
`s2idle`. NVIDIA documenta un modo S0ix específico que puede activarse si **la
plataforma y la GPU** soportan `Video Memory Self Refresh`.

Antes de habilitarlo hay que consultar, durante una activación limpia:

```text
/proc/driver/nvidia/gpus/0000:01:00.0/power
```

Solo si informa soporte corresponde probar:

```text
options nvidia NVreg_EnableS0ixPowerManagement=1
```

Se activó `NVreg_EnableS0ixPowerManagement=1`, se regeneró initramfs y se confirmó
el parámetro efectivo. La reanudación continuó bloqueándose. S0ix queda descartado
como solución aislada.

### 5. Audio XG desvinculado y retirado — completado, negativo

Se probaron por separado el unbind de `0000:01:00.1` de `snd_hda_intel` y la
eliminación completa de esa función PCI, manteniendo video `0000:01:00.0`
vinculado a NVIDIA. Ambas suspensiones reales se bloquearon. El audio XG queda
descartado como causa aislada y no corresponde construir un workaround alrededor
de su unbind/rebind.

### 6. Notificador ACPI NVIDIA — descartado como condición suficiente

`pm_test=devices` pasa. `pm_test=platform` también vuelve rápidamente cuando se
ejecuta mediante systemd, aunque registra `RmHandleDNotifierEvent` con
`NV_ERR_GPU_NOT_FULL_POWER`. Como el mismo evento es compatible con un resume de
kernel de 0.206 segundos, el quirk que omite el handler ACPI queda congelado y no
debe compilarse ni instalarse sin evidencia nueva.

Orden obligatorio para no confundir otro artefacto con la causa:

1. Mantener la RTX activa y sana, sin tareas `D`.
2. Escribir `platform` en `/sys/power/pm_test`.
3. Disparar la suspensión con `systemctl suspend`, no escribiendo directamente
   `mem` en `/sys/power/state`.
4. Confirmar en journal que `nvidia-suspend.service` terminó antes de
   `PM: suspend entry` y que `nvidia-resume.service` se ejecutó al volver.
5. Confirmar físicamente que la imagen y la sesión GNOME siguen utilizables; SSH,
   `nvidia-smi` y ausencia de tareas `D` no alcanzan.
6. Una suspensión real queda prohibida hasta satisfacer los cinco puntos.

### 7. A/B sin `amd_pmc` — resume real confirmado

La documentación oficial AMD recomienda desvincular temporalmente `amd_pmc` para
evitar la transición S0i3 y poder observar restricciones ACPI. El primer intento
usó un servicio transitorio con un `trap` de rebind alrededor de
`systemctl suspend`. El journal demuestra que `systemctl` devolvió de inmediato,
el servicio transitorio terminó y ejecutó el rebind **antes** de que comenzaran
`nvidia-suspend.service` y `PM: suspend entry`. La máquina volvió a bloquearse con
PMC normal; este intento no prueba nada sobre `amd_pmc`. Además pudo partir de una
sesión gráfica ya degradada.

La repetición válida usó un hook temporal de `systemd-sleep`:

1. `pre/suspend`: desvincular `AMDI0005:00` de `amd_pmc`.
2. `post/suspend`: volver a vincularlo.
3. Validar primero el hook con `pm_test=platform` y confirmar por journal que no
   ejecutó callbacks de `amd_pmc` durante la ventana.
4. Exigir confirmación humana de imagen y sesión después del test simulado.
5. Solo entonces considerar un s2idle real con alarma RTC y recuperación física.

El hook se validó primero con `pm_test=platform`: el journal confirmó `pre` y
`post`, no hubo callbacks PMC durante la ventana, dispositivos reanudaron en
0.206 segundos y el usuario confirmó pantalla, teclado y sesión gráfica.

Luego se ejecutó un ciclo real con una alarma RTC a 20 segundos:

```text
13:59:15  nvidia-suspend.service iniciado
13:59:16  nvidia-suspend.service terminado
13:59:16  hook pre: amd_pmc desvinculado de AMDI0005:00
13:59:16  PM: suspend entry (s2idle)
13:59:35  PM: suspend devices took 0.106 seconds
13:59:35  PM: resume devices took 0.209 seconds
13:59:35  PM: suspend exit
13:59:35  hook post: amd_pmc revinculado a AMDI0005:00
13:59:36  nvidia-resume.service terminado
```

El usuario confirmó que la pantalla encendió. La misma sesión GNOME continuó,
la RTX 3080 siguió visible con 16 GiB, no hubo tareas `D`, el marker temporal se
eliminó y `amd_pmc` quedó vinculado otra vez. El D-Notifier `0x11` apareció, pero
no bloqueó el resume.

Se validó después el caso operativo principal, cierre y apertura de tapa, con una
alarma RTC de respaldo que no fue necesaria para disparar el wake:

```text
14:02:17  systemd-logind: Lid closed
14:02:19  hook pre: amd_pmc desvinculado
14:02:19  PM: suspend entry (s2idle)
14:02:40  systemd-logind: Lid opened
14:02:40  PM: resume devices took 0.208 seconds
14:02:40  PM: suspend exit
14:02:40  hook post: amd_pmc revinculado
14:02:41  nvidia-resume.service terminado
```

La pantalla encendió, GNOME mantuvo el mismo PID, la RTX quedó sana en P8 y
`suspend_stats` avanzó a `success=3`, `fail=0`. Este es el primer workaround
confirmado para cerrar la tapa con la XG activa sin perder sesión ni requerir un
corte forzado.

### Dependencia PCIe global descartada para suspensión — A/B final

El 2026-08-20 se quitó `pcie_port_pm=off` solamente de la entrada normal del
kernel 7.1.8; la entrada 6.19 quedó intacta como recuperación. Se instaló una
regla udev limitada al hardware exacto que conserva:

```text
0000:00:01.1/power/control=on
vendor=1022 device=1633 subsystem=1043:1662
```

El primer arranque con la XG físicamente conectada y desactivada demoró 2:04. El
initrd consumió 1:28 reintentando durante 88 segundos `usb 1-1.1`, que luego se
identificó como el segundo hub Genesys Logic `05e3:0610` de la XG. En ese mismo
arranque la inicialización NVIDIA de la GTX 1650 quedó bloqueada en
`kgspInitRm_IMPL`; udev, `nvidia-powerd` y las consultas RM quedaron en estado
`D`, lo que también trabó el reinicio. No era el mouse: éste estaba conectado a
otro puerto USB.

Se hizo un reset eléctrico/EC de 40 segundos, se arrancó sin XG y la GTX 1650
quedó sana en P8, sin tareas `D`. Al conectar después la XG sin activarla, ambos
hubs USB enumeraron normalmente y la GTX siguió respondiendo. La activación en
caliente mediante `ghelperd` completó HID, rescan y cambio a la RTX 3080 sin
reiniciar la sesión.

Con esa RTX activa, el hook PMC instalado y **sin** `pcie_port_pm=off`, se cerró
la tapa a las 14:35:20 y se abrió a las 14:37:05:

```text
14:35:23  hook pre: amd_pmc desvinculado de AMDI0005:00
14:35:23  PM: suspend entry (s2idle)
14:37:05  Lid opened
14:37:05  PM: suspend devices took 0.107 seconds
14:37:05  PM: resume devices took 0.207 seconds
14:37:05  PM: suspend exit
14:37:05  hook post: amd_pmc revinculado a AMDI0005:00
```

Volvió la imagen y sobrevivió la misma sesión GNOME. `egpu_enable` continuó en
1, la RTX respondió con 16 GiB, no hubo tareas `D` y `suspend_stats` informó
`success=1`, `fail=0`. Después de la actividad inicial de resume la RTX volvió a
P8 sin holders de usuario.

Resultado: el workaround de suspensión no necesita el argumento global
`pcie_port_pm=off`; alcanza el hook que evita el handoff PMC/SMU, mientras la
política puntual del root port conserva el hot-switch. El arranque frío con la
XG conectada todavía exhibe estado intermitente de hub/GSP y debe tratarse como
un problema separado.

### Notificación HID de suspensión usada por Windows

El código público de G-Helper para Windows reveló un par adicional dirigido al
ITE `0b05:1970` de la XG:

```text
pre/suspend:  5E-E4-01
post/resume:  5E-41-53-55-53-20-54-65-63-68-2E-49-6E-63-2E
post/resume:  5E-E4-02
```

Windows llama al primero `NotifyShutdown()` y lo envía tanto al suspender como
al apagar la pantalla. En Linux se añadió al hook existente sin retirar todavía
la protección que desvincula `amd_pmc`.

Después de descartar un primer intento inválido causado por una copia ejecutable
del hook en `system-sleep`, se ejecutó un ciclo limpio con un único hook. La RTX
partió en P0, 65 C y unos 40 W. La tapa se cerró a las 16:42:27; `E4-01` se envió
y PMC se desvinculó a las 16:42:29. El cooler continuó inicialmente, pero se
detuvo mientras la tapa todavía estaba cerrada; el LED permaneció encendido.

Al abrir a las 16:45:25, Linux reanudó en 0.206 segundos, revinculó PMC y envió
la autenticación más `E4-02`. Volvieron imagen y cooler, la misma sesión GNOME
continuó, y la RTX quedó sana en P8, 13.21 W y 53 C. No hubo tareas `D` y
`suspend_stats` avanzó a `success=4`, `fail=0`.

Esto reproduce la intención observable del flujo Windows: el controlador del
dock recibe una notificación de reposo y puede detener el cooler sin cortar la
alimentación ni apagar el LED; al reanudar se reinicializa. Todavía falta el A/B
causal sin la notificación y a igual temperatura, y luego la prueba riesgosa que
mantiene PMC vinculado para comprobar si el aviso permite S0i3 real.

`/sys/power/suspend_stats` informó `success=2`, `fail=0` y
`last_hw_sleep=0`. Esto es coherente con el propósito del A/B: Linux completó
s2idle, pero sin entregar la transición profunda S0i3 al SMU mediante `amd_pmc`.
El workaround recupera funcionalidad de suspensión/reanudación con XG activa;
todavía no garantiza consumo bajo durante una suspensión prolongada.

La causa queda aislada a la interacción del handoff `amd_pmc`/SMU con la XG
activa. No es el audio HDMI, no es el D-Notifier por sí solo y no son los callbacks
normales de dispositivos. El arreglo de fondo requiere descubrir qué constraint,
evento o secuencia de firmware bloquea S0i3; el hook es una mitigación funcional.

Hook actualmente instalado:

```text
/usr/lib/systemd/system-sleep/ghelper-amd-pmc-ab
SHA256 240b03a95e2d9f0001ef901b36c6b5961b72d6e23aa2a3caf79eb1bc4ab6795b
/usr/libexec/ghelper/ghelper-xg-hid-power.py
SHA256 e7af8314fe86729c06dc4a61dc675a276355d2dbbc105b9ccd084af872595e23
```

Solo actúa en el GV301QH exacto y cuando `egpu_enable=1`. Rollback:

```text
sudo rm /usr/lib/systemd/system-sleep/ghelper-amd-pmc-ab
sudo rm /usr/libexec/ghelper/ghelper-xg-hid-power.py
```

### 8. Módulo propietario y `nvidia-powerd`

El bloqueo de arranque en frío involucra a `nvidia-powerd` y el lock de RM. Deben
hacerse dos A/B separados:

- arrancar con `nvidia-powerd` deshabilitado;
- comparar el open kernel module con el módulo propietario de la misma versión.

Esto apunta principalmente al segundo fallo. No explica por sí solo la suspensión,
porque la reproducción de `s2idle` ocurrió después de que el ejecutor hubiera
detenido `nvidia-powerd` durante la activación.

### 9. Guardia de arranque temprano

Aunque se arregle la suspensión, GHelper debe evitar que una XG marcada activa
llegue al autoload normal de NVIDIA después de un corte abrupto. Si el driver no
puede corregirse, hará falta una unidad/initramfs temprana que, antes de cargar
NVIDIA, fuerce el estado seguro o difiera el autoload hasta reconciliar XG y PCI.

La recuperación confirmada después de un corte con XG activa usa, en un arranque
temporal, ambos parámetros:

```text
module_blacklist=nvidia,nvidia_drm,nvidia_modeset,nvidia_uvm,nvidia_peermem
systemd.mask=nvidia-powerd.service
```

`rd.driver.blacklist` y `modprobe.blacklist` solos no alcanzan frente a cargas
explícitas. Una vez iniciado sin NVIDIA, el daemon puede desactivar la XG y el
arranque normal vuelve a la GTX 1650.

## Estado después del A/B final del 2026-08-20

```text
boot_id: 8c90872f-7488-4d58-8f34-03f3af66f1fc
XG físicamente conectada: 1
egpu_enable: 1
GPU: RTX 3080, 16384 MiB, P8
tareas D: 0
kernel: 7.1.8-200.fc44.x86_64
pcie_port_pm=off: ausente
0000:00:01.1/power/control: on
EnableS0ixPowerManagement: 1
UseKernelSuspendNotifiers: 0
PreserveVideoMemoryAllocations: 2
```

El A/B de notificador dejó temporalmente instalado:

```text
/etc/modprobe.d/ghelper-xg-s0ix.conf
```

Hay rollback en:

```text
/etc/modprobe.d/ghelper-xg-s0ix.conf.pre-userspace-resume
/boot/initramfs-7.1.8-200.fc44.x86_64.img.ghelper-pre-s0ix
```

Para retomar, repetir ciclos cortos de tapa con esta misma configuración y medir
consumo durante una suspensión prolongada. No repetir el A/B de audio ni el
cambio de notificador: ambos ya quedaron descartados como soluciones aisladas.

## Revalidación después de la instalación limpia — 2026-08-21

La instalación nueva de Fedora 44 reprodujo el hot-switch completo y permitió
revalidar el workaround de tapa con la configuración exacta anterior:

```text
UseKernelSuspendNotifiers: 0
EnableS0ixPowerManagement: 1
pcie_port_pm=off
XG: egpu_enable=1, RTX 3080 16384 MiB
```

Una alarma RTC de respaldo despertó el equipo mientras la tapa seguía cerrada.
El journal mostró `E4-01`, unbind de `AMDI0005:00`, entrada a s2idle, resume de
dispositivos en 0.209 segundos, rebind de PMC, autenticación HID y `E4-02`.
Después de abrir la tapa el usuario confirmó imagen y entrada; GNOME conservó el
mismo PID, la RTX respondió, no hubo tareas `D` ni errores Xid/GSP y
`suspend_stats` quedó en `success=1`, `fail=0`.

La misma sesión descubrió además un holder independiente: al cargar
`nvidia_drm`, `xdg-desktop-portal-gnome` abría el render node NVIDIA aunque
Mutter respetara `mutter-device-ignore`. Forzar únicamente ese servicio a usar
`50_mesa.json` eliminó los holders; con el portal activo se pudo cargar y
descargar `nvidia_drm`. Este override pasa a formar parte de la instalación.

## Consolidación de arranque y apagado — 2026-08-21

El apagado bloqueado no provenía de systemd ni del desmontaje de `/home`: la GUI
podía quedar esperando sondeos NVIDIA al recibir SIGTERM. El runtime instalado
ahora usa un cierre acotado para señales, y toda ejecución de `nvidia-smi` pasa
por una única compuerta con timeout y terminación del árbol de procesos.

Se comprobaron apagados reales en ambos estados:

```text
XG conectada/desactivada: apagado limpio en aproximadamente 3.4 s
XG activa/RTX 3080:       apagado limpio en aproximadamente 1.7 s
```

En la prueba activa la GUI registró SIGTERM, completó el cierre acotado y su
scope terminó unos 29 ms después. `/home` se desmontó normalmente y no quedaron
consultas NVIDIA ni tareas `D`.

Durante la restauración del estado Standard se detectó un residuo de una prueba
Eco anterior: NVIDIA seguía incluido en `rd.driver.blacklist` y
`modprobe.blacklist`, además de un archivo temporal
`91-x13-nvidia-deferred.conf` dentro del initramfs. Se retiraron solamente esos
bloqueos de NVIDIA, se conservaron los de `nouveau,nova_core` y se regeneró el
initramfs. El reboot de control cargó la GTX 1650 automáticamente con
`nvidia 610.57.04`, sin `modprobe` manual, sin tareas `D` y sin
`pcie_port_pm=off`.

El diff desplegado cerró además la aceptación reproducible completa: 145
escenarios C#, 93 escenarios de arranque, 12 pruebas de audio y dos builds
Native AOT idénticos. El artefacto verificado tiene SHA-256
`7b93ebb5928e27f4518e18a46cb7c0db5ac82f2028506c9a20e55adcbd2293c6`.

## Criterios para declarar una solución

No alcanza con recuperar imagen una vez. La solución debe completar:

- cinco ciclos consecutivos de suspensión/reanudación con XG activa;
- reinicio ordenado desde XG activa y arranque sin tareas `D`;
- recuperación después de un corte forzado con XG conectada;
- `nvidia-smi`, Vulkan PRIME y Dota funcionales después de reanudar;
- misma sesión Wayland cuando el camino no exige reinicio;
- journal sin `Xid`, `rmapiLockIsOwner`, heartbeat timeout ni tareas bloqueadas.

## Referencias

- Linux kernel, `pm_trace` y decodificación de fingerprints:
  https://www.kernel.org/doc/html/v5.3/power/s2ram.html
- Linux kernel, implementación del hash y los tres campos del magic number:
  https://github.com/torvalds/linux/blob/master/drivers/base/power/trace.c
- NVIDIA, power management:
  https://download.nvidia.com/XFree86/Linux-aarch64/595.58.03/README/powermanagement.html
- NVIDIA open kernel modules #1117, suspensión rota en kernel 7.x y funcional en
  6.17: https://github.com/NVIDIA/open-gpu-kernel-modules/issues/1117
- NVIDIA open kernel modules #1125, RTX 3080 y fallo de reanudación por GSP:
  https://github.com/NVIDIA/open-gpu-kernel-modules/issues/1125
- NVIDIA open kernel modules #1271, fallo de reanudación con driver 610:
  https://github.com/NVIDIA/open-gpu-kernel-modules/issues/1271
- NVIDIA open kernel modules #1142, Fedora 44 y la misma firma D-Notifier; allí
  la causa era una denegación SELinux al guardado de VRAM, no observada en esta
  X13: https://github.com/NVIDIA/open-gpu-kernel-modules/issues/1142

# XG Mobile en caliente en Linux — ROG Flow X13 GV301QH

**Estado:** MVP funcional e instalable para el GV301QH

**Fecha:** 2026-08-19

**Equipo probado:** ASUS ROG Flow X13 GV301QH + XG Mobile RTX 3080 16 GiB

Esta carpeta es el punto de entrada para la investigación, el código y la
evidencia del soporte XG Mobile del GV301QH. El build canónico ya entrega GUI y
daemon juntos, y `scripts/ghelper-xg-mvp.sh` administra instalación, estado,
migración desde el POC y desinstalación sin rutas de usuario hardcodeadas.

## Resultado

Se consiguió alternar en vivo, en ambos sentidos:

```text
GTX 1650 interna -> RTX 3080 XG Mobile -> GTX 1650 interna
```

sin reiniciar la máquina, sin detener GDM y sin destruir la sesión Wayland. La
sesión `46` y el proceso `gnome-shell` PID `20707` permanecieron iguales durante
todo el ciclo.

La solución necesitó resolver dos problemas independientes:

1. El firmware/enlace PCIe no completaba la transición mientras Linux administraba
   energía de los puertos PCIe. El workaround mínimo confirmado es
   `pcie_port_pm=off`.
2. Aunque GNOME renderizaba el escritorio sobre la iGPU AMD, Mutter abría y retenía
   las dos GPU NVIDIA secundarias. Etiquetarlas con `mutter-device-ignore` permite
   descargar el driver NVIDIA sin terminar la sesión.

Ninguno de los dos cambios por separado era suficiente.

## Hardware y software verificados

```text
Laptop:          ROG Flow X13 GV301QH_GV301QH
BIOS:            GV301QH.418, 2025-12-24
XG Mobile:       RTX 3080 Laptop GPU, 16 GiB, PCI ID 10de:249c
GPU interna:     GTX 1650 Mobile / Max-Q, PCI ID 10de:1f9d
iGPU/escritorio: AMD Cezanne, PCI ID 1002:1638
Sistema:         Fedora Linux 44
Kernel:          7.1.8-200.fc44.x86_64
NVIDIA:          610.57.04, módulo abierto
GNOME/Mutter:    50.4
Sesión:          GNOME Wayland
```

Ambas NVIDIA aparecen en el mismo BDF, `0000:01:00.0`, según el estado del XG:
el firmware reemplaza un endpoint PCIe por el otro.

## Descubrimiento 1: energía del puerto PCIe

### Fallo original

Con la línea de kernel normal de Fedora, el write a `egpu_enable` quedaba bloqueado
unos 30 segundos. El kernel informaba:

```text
asus_wmi: Failed to set egpu state (retval): 0x2
```

La RTX no enumeraba. Hubo intentos que terminaron en sesión/reinicio colgado y uno
en reset abrupto de firmware.

### Prueba A/B

Se ensayaron estos parámetros:

- `pcie_port_pm=off pcie_aspm=off`: transición exitosa.
- solo `pcie_port_pm=off`: transición exitosa.
- `pcie_aspm=off` no resultó necesario.

La configuración mínima efectiva y persistente es:

```text
pcie_port_pm=off
```

El 2026-08-19 la línea efectiva era:

```text
BOOT_IMAGE=(hd0,gpt2)/vmlinuz-7.1.8-200.fc44.x86_64 root=UUID=d6ccd6aa-f49d-4410-825c-4b956ebfc414 ro rootflags=subvol=root rhgb quiet rd.driver.blacklist=nouveau,nova_core modprobe.blacklist=nouveau,nova_core pcie_port_pm=off
```

El parámetro quedó persistido en la configuración de kernel/GRUB de la X13.

### Resultado PCIe

Con el workaround, la RTX enumeró como:

```text
pci 0000:01:00.0: [10de:249c] type 00 class 0x030000 PCIe Legacy Endpoint
pci 0000:01:00.0: 63.008 Gb/s available PCIe bandwidth, limited by 8.0 GT/s PCIe x8 link at 0000:00:01.1
```

## Descubrimiento 2: GNOME retenía NVIDIA

Antes de la corrección, AMD ya era la GPU primaria y manejaba el panel interno,
pero `gnome-shell` mantenía abiertos:

```text
/dev/dri/card0
/dev/dri/renderD129
/dev/nvidia0
/dev/nvidiactl
/dev/nvidia-modeset
```

El PID observado era `6644`. Esto impedía descargar `nvidia_drm`,
`nvidia_modeset` y `nvidia`, de modo que la primera implementación tenía que hacer:

```text
systemctl stop display-manager.service
```

En Wayland, matar el compositor destruye las conexiones de todas las aplicaciones.
No existe una forma práctica de “persistir” esa misma sesión después de matar
`gnome-shell`; había que evitar que GNOME tomara NVIDIA desde el principio.

## Solución de sesión persistente: `mutter-device-ignore`

Mutter 50.4 instalado en Fedora contiene soporte para el tag udev
`mutter-device-ignore`. Mutter lo consulta tanto durante la enumeración inicial
como al agregar una GPU secundaria en caliente.

La regla instalada es [61-mutter-ignore-x13-nvidia.rules](../../packaging/udev/61-mutter-ignore-x13-nvidia.rules):

```udev
SUBSYSTEM=="drm", ENV{DEVTYPE}=="drm_minor", ENV{DEVNAME}=="/dev/dri/card[0-9]", SUBSYSTEMS=="pci", ATTRS{vendor}=="0x10de", ATTRS{device}=="0x1f9d", TAG+="mutter-device-ignore"
SUBSYSTEM=="drm", ENV{DEVTYPE}=="drm_minor", ENV{DEVNAME}=="/dev/dri/card[0-9]", SUBSYSTEMS=="pci", ATTRS{vendor}=="0x10de", ATTRS{device}=="0x249c", TAG+="mutter-device-ignore"
```

Se usan IDs de dispositivo concretos para no esconder cualquier NVIDIA que pudiera
conectarse en el futuro.

Después de instalar la regla fue necesario reiniciar la sesión gráfica una sola
vez. La nueva instancia de GNOME registró:

```text
Ignoring DRM device '/dev/dri/card0'
Added device '/dev/dri/card1' (amdgpu) using atomic mode setting.
Created gbm renderer for '/dev/dri/card1'
GPU /dev/dri/card1 selected primary from builtin panel presence
```

Resultado:

```text
gnome-shell PID:        20707
descriptores NVIDIA:    0
GPU del escritorio:     AMD Cezanne
```

La etiqueta no impide que aplicaciones individuales abran NVIDIA. La RTX continúa
publicada por `switcheroo-control` como GPU discreta para PRIME Render Offload.

## Secuencia funcional implementada

El ejecutor está en
[XgMobileMutationExecutor.cs](../../daemon/Hardware/XgMobileMutationExecutor.cs).

### Preflight

1. Exige modelo `GV301QH`.
2. Exige los atributos ACPI `egpu_connected` y `egpu_enable`.
3. Exige que el XG esté físicamente conectado.
4. Solo permite una transición simultánea.
5. Rechaza la operación si existe más de una GPU NVIDIA visible.

### Liberación segura

1. Detiene `nvidia-powerd.service` y `nvidia-persistenced.service`.
2. Busca procesos con FDs abiertos bajo `/dev/nvidia*`.
3. Espera hasta 20 segundos.
4. Si queda un holder, aborta antes de modificar ACPI y muestra `PID/comm`.
5. Desvincula la función de audio HDMI de `snd_hda_intel`.
6. Descarga, en orden, `nvidia_uvm`, `nvidia_drm`, `nvidia_modeset`,
   `nvidia_peermem` y `nvidia`.

El daemon no mata aplicaciones. Dota, Steam, CUDA u otra aplicación que esté usando
NVIDIA debe cerrarse antes de retirar la GPU.

### Activación

1. Escribe `1` en `/sys/devices/platform/asus-nb-wmi/egpu_enable`.
2. Si ASUS WMI devuelve `EIO/0x2`, registra el error pero continúa con la
   inicialización HID, igual que G-Helper oficial en Windows.
3. Localiza el HID ASUS XG Mobile `0b05:1970`.
4. Envía los feature reports:

   ```text
   5E-41-53-55-53-20-54-65-63-68-2E-49-6E-63-2E
   5E-E4-02
   5E-C5-50
   5E-BD-00-01
   ```

5. Espera 15 segundos, como el flujo original.
6. Escribe `1` en `/sys/bus/pci/rescan` y espera udev.
7. Verifica que aparezca una NVIDIA distinta de `10de:1f9d`.
8. Carga el módulo NVIDIA.
9. Confirma `egpu_enable=1`.

### Desactivación

1. Repite la liberación segura de holders, audio y módulos.
2. Envía el reset HID del XG desde la GUI antes de retirar el endpoint.
3. Escribe `0` en `egpu_enable`.
4. Espera que desaparezca la RTX.
5. El firmware vuelve a exponer la GTX 1650 en `0000:01:00.0`.

### Diferencia decisiva frente al primer POC

Ya no se detiene ni reinicia `display-manager.service`. La sesión gráfica debe
permanecer viva; si no puede liberarse NVIDIA, la transición falla cerrada.

## Evidencia del ciclo exitoso

### Estado inicial

```text
session=46
gnome_pid=20707
egpu_connected=1
egpu_enable=0
0000:01:00.0 10de:1f9d GTX 1650 Mobile / Max-Q
GNOME NVIDIA holders: none
```

### Activación, 03:45:48–03:46:10

```text
ghelperd: Starting live XG Mobile transition: False -> True.
ghelperd: XG Mobile HID: ... 0B05:1970 ... hidraw6
ghelperd: XG Mobile live transition completed: enabled=True.
```

Verificación:

```text
egpu_enable=1
0000:01:00.0 10de:249c RTX 3080 Laptop GPU
GTX 1650: absent
gnome_pid=20707
GNOME NVIDIA holders: none
```

`nvidia-smi`:

```text
NVIDIA GeForce RTX 3080 Laptop GPU
VRAM: 16384 MiB
Driver: 610.57.04
P-state en reposo: P8
```

Smoke real de Vulkan mediante `switcherooctl launch vulkaninfo --summary`:

```text
GPU0:
    deviceName = NVIDIA GeForce RTX 3080 Laptop GPU
    driverName = NVIDIA
    driverInfo = 610.57.04
```

### Desactivación, 03:47:13–03:47:18

```text
ghelperd: Starting live XG Mobile transition: True -> False.
ghelperd: XG Mobile live transition completed: enabled=False.
```

Verificación:

```text
egpu_enable=0
RTX 3080: absent
0000:01:00.0 10de:1f9d GTX 1650 Mobile / Max-Q
gnome_pid=20707
session=46
GNOME NVIDIA holders: none
```

La identidad de sesión y compositor fue idéntica antes, durante y después del
ciclo.

## Qué se aprendió del driver y del firmware público

Existe un checkout de investigación separado en:

```text
/home/andres/Claude/XG_Mobile_Station
```

No se mueve ni se incorpora aquí porque es un proyecto externo con su propio Git
y licencia.

### `XGMDriver` no implementa el hotplug

El driver Windows incluido allí es esencialmente un dispositivo HID virtual:

- Maneja descriptores, reportes HID, `GET_FEATURE` y `SET_FEATURE`.
- `SetFeature` valida el report ID/tamaño y copia el último reporte a memoria.
- `IOCTL_HID_ACTIVATE_DEVICE` y `IOCTL_HID_DEACTIVATE_DEVICE` terminan en
  `STATUS_NOT_IMPLEMENTED`.

Por lo tanto, portar ese driver a Linux no habría resuelto la transición PCIe.
Los feature reports son una parte de la secuencia, no el controlador completo del
enlace.

### El BIOS confirma que el cambio es coordinado por firmware

El `Docs/BIOS_Detect.c` decompilado contiene `reconnect_xgm`. La rutina:

- Comprueba estados de conexión, traba y energía mediante EC.
- Modifica estados `GPUM` y `UMAF`.
- Manipula registros de enlace PCIe con esperas intermedias.
- Ordena la reconexión al EC y espera que el estado se estabilice.
- Tiene flujo de retry/restart si la conexión no cumple las condiciones.

Esto confirma que no existe un único “comando mágico” de driver. La solución Linux
debe coordinar firmware/ACPI, HID, lifecycle del driver, rescan PCI y compositor.

### Investigación de BIOS ASUS

El único archivo persistente localizado al cerrar esta jornada es:

```text
/home/andres/Descargas/GV301QHAS418.zip
```

La revisión de firmware no produjo una interfaz nueva directamente utilizable. El
avance decisivo vino de observar el comportamiento en vivo y aislar energía de
puerto PCIe. Si mañana se retoma la comparación con BIOS anteriores, hay que
volver a conservar los binarios y hashes en esta carpeta; los temporales de las
versiones viejas no deben considerarse evidencia persistente.

## Fotografía histórica previa a la consolidación

Fotografía tomada el 2026-08-19 03:51 -03:00:

```text
XG físicamente conectado: 1
XG lógicamente activo:    0
GPU visible:              GTX 1650 10de:1f9d
Sesión:                   46
gnome-shell:              PID 20707
FDs NVIDIA de GNOME:      0
ghelperd:                 active
```

Archivos instalados:

```text
/usr/libexec/ghelper/ghelperd
/etc/systemd/system/ghelperd.service
/etc/dbus-1/system.d/org.ghelper.Daemon1.conf
/usr/share/polkit-1/actions/org.ghelper.daemon.policy
/etc/udev/rules.d/61-mutter-ignore-x13-nvidia.rules
```

Hashes del POC que estaba instalado en esa fotografía:

```text
ghelperd: ba8dc72cb6b1cf6d14a86bd4060d71600fde1e08d910760d1d63f29a64b645f4
udev:     33c7e10e9f1b7f1994f6d5533fe0a86aae4bcc365d7c8c8c515498f18c1ff984
```

Staging histórico en la laptop:

```text
/home/andres/.local/share/ghelper-xg-live-stage
```

## Archivos del desarrollo

- [Ejecutor XG](../../daemon/Hardware/XgMobileMutationExecutor.cs)
- [Inicialización HID](../../daemon/Hardware/XgMobileHid.cs)
- [Regla Mutter](../../packaging/udev/61-mutter-ignore-x13-nvidia.rules)
- [Instalador/status/uninstaller del MVP](../../scripts/ghelper-xg-mvp.sh)
- [Política PolicyKit](../../packaging/polkit/org.ghelper.daemon.policy)
- [Unidad systemd](../../packaging/systemd/ghelperd.service)
- [Cliente D-Bus](../../src/Daemon/GHelperDaemonClient.cs)
- [Botón/UI](../../src/UI/Views/MainWindow.axaml.cs)

El desarrollo consolidado vive en:

```text
/home/andres/GitHub/g-helper-linux-x13
branch: x13-hardened
```

El build, la instalación y el rollback canónicos están versionados en este mismo
árbol. No debe publicarse una release sin reconstruir el artefacto desde el commit
que se quiera distribuir y revisar su manifiesto.

Artefactos históricos, reemplazados por el build consolidado:

```text
/home/andres/ghelper-x13-poc-live-xg-v1
/home/andres/ghelper-x13-poc-dist
/home/andres/Descargas/GV301QHAS418.zip
/home/andres/Claude/XG_Mobile_Station
```

## Operación cotidiana

### Usar la RTX

La sesión permanece sobre AMD. Una aplicación puede lanzarse sobre NVIDIA con
`switcheroo-control`; por ejemplo:

```bash
switcherooctl launch steam
```

El entorno publicado para la RTX fue:

```text
__GLX_VENDOR_LIBRARY_NAME=nvidia
__NV_PRIME_RENDER_OFFLOAD=1
__VK_LAYER_NV_optimus=NVIDIA_only
VK_LOADER_DRIVERS_SELECT=*nvidia*
```

### Antes de desactivar el XG

Cerrar todo lo que use NVIDIA. El daemon debe listar y rechazar holders en vez de
matarlos. Esto incluye juegos, procesos CUDA, herramientas de monitoreo persistente
y eventualmente Xwayland si alguna aplicación lo hace abrir NVIDIA.

## Limitaciones conocidas

1. Los monitores conectados físicamente a las salidas de la XG probablemente no
   funcionen mientras Mutter ignore la RTX. El modo probado usa el panel interno
   manejado por AMD y NVIDIA solo como render offload.
2. Solo se validó formalmente el GV301QH con GTX 1650 `1f9d` y XG RTX 3080 `249c`.
3. Hace falta probar suspensión/reanudación y un juego real antes
   de llamar estable al flujo.
4. El MVP es reproducible localmente, pero todavía no es un RPM firmado ni una release.

## Instalación y rollback

```bash
sudo ./scripts/ghelper-xg-mvp.sh install /ruta/absoluta/ghelper-xg-mvp-dist "$USER"
./scripts/ghelper-xg-mvp.sh status "$USER"
sudo ./scripts/ghelper-xg-mvp.sh uninstall "$USER"
```

El estado root-owned registra si `pcie_port_pm=off` ya existía o si fue agregado
por el MVP. El rollback sólo retira el parámetro en el segundo caso. La regla de
Mutter, el daemon, D-Bus, polkit, autostart y GUI se eliminan por rutas exactas;
la configuración del usuario se conserva.

## Próximos pasos

Prioridad funcional después de instalar el MVP consolidado:

1. Mostrar en la app qué procesos impiden desconectar NVIDIA.
2. Ejecutar Dota mediante PRIME y validar rendimiento/VRAM.
3. Probar suspensión y reanudación en ambos estados.
4. Decidir UX para monitores conectados a la XG: modo persistente sin outputs o
   modo de relogin que permita a Mutter manejar las salidas.
5. Actualizar los reportes públicos con el segundo hallazgo de Mutter una vez que
   el ciclo repetido y Dota estén validados.

Hardening posterior:

- Paquete RPM firmado y manifiesto de archivos.
- Attestation del daemon y política PolicyKit final.
- Recuperación ante transición interrumpida.
- Tests de holders, timeouts y rollback.
- Detección explícita de `pcie_port_pm=off` antes de ofrecer el botón.
- Soporte por tabla de modelos/PCI IDs en vez de constantes del GV301QH.

## Reportes públicos

- G-Helper Linux port: https://github.com/utajum/g-helper-linux/issues/171
- Repositorio ASUS Linux activo: https://github.com/OpenGamingCollective/asusctl/issues/320
- Reporte histórico archivado: https://gitlab.com/asus-linux/supergfxctl/-/work_items/136

El issue histórico no admite comentarios porque el proyecto GitLab está archivado.
El desarrollo activo de ASUS Linux migró a OpenGamingCollective.

## Sobre la novedad del hallazgo

En los repositorios y reportes revisados no se encontró una implementación que
combine:

```text
pcie_port_pm=off
+ secuencia ACPI/HID/rescan
+ mutter-device-ignore
+ PRIME Render Offload
```

para conseguir el cambio bidireccional del XG Mobile sin reiniciar ni destruir la
sesión GNOME en un GV301QH.

Eso es evidencia fuerte de que el workaround no estaba documentado en los proyectos
consultados, pero no demuestra que nadie en toda la comunidad Linux lo haya hecho
de manera privada o en otro canal. Para comunicarlo públicamente conviene decir
“no encontramos una solución equivalente en los repositorios consultados”, no
afirmar prioridad universal hasta ampliar la búsqueda y recibir feedback de los
maintainers.

## Referencias técnicas

- Mutter, multi-GPU: https://gitlab.gnome.org/GNOME/mutter/-/blob/main/doc/multi-gpu.md
- Mutter, filtro udev de dispositivos: https://gitlab.gnome.org/GNOME/mutter/-/blob/main/src/backends/native/meta-backend-native.c
- NVIDIA PRIME Render Offload: https://download.nvidia.com/XFree86/Linux-x86_64/455.45.01/README/primerenderoffload.html
- NVIDIA external/removable GPUs: https://download.nvidia.com/XFree86/Linux-x86_64/396.45/README/egpu.html
- Linux DRM hot-unplug: https://docs.kernel.org/5.10/gpu/drm-uapi.html#device-hot-unplug

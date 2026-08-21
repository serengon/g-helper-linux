# Plan de consolidación de G-Helper para la Flow X13

**Objetivo:** convertir el MVP ya validado en una versión final, instalable y
usable diariamente en la combinación probada:

- ASUS ROG Flow X13 GV301QH
- BIOS 418
- Fedora Workstation 44
- XG Mobile 2021 con NVIDIA GeForce RTX 3080

La primera versión final estará soportada solamente para esta combinación. La
portabilidad a otras distribuciones, kernels, modelos de Flow o generaciones de
XG Mobile se abordará después de cerrar el producto funcional.

## Principio de trabajo

La funcionalidad tiene prioridad. Primero se consolidará el camino que ya fue
probado físicamente en la X13; después se completarán el hardening, la firma y
la distribución general.

No se considerará terminada una función porque compile o pase una prueba
aislada. Debe funcionar en la laptop real y dejar un estado verificable en la
GUI y en los logs.

## Estado de partida

El commit `707c71d` (`feat(x13): consolidate XG Mobile MVP`) contiene la línea
base funcional actual:

- GUI ejecutada como usuario normal;
- `ghelperd` como frontera privilegiada separada;
- activación y desactivación en caliente de la XG Mobile;
- autorización mediante D-Bus y polkit;
- detección del modelo y de los atributos ASUS WMI;
- liberación y recuperación de los dispositivos NVIDIA;
- protección contra procesos que retienen `/dev/nvidia*`;
- assets de systemd, D-Bus, polkit, udev y autostart;
- instalador y desinstalador específicos del MVP.

La investigación posterior demostró además que el bloqueo al reanudar con la
XG activa se evita desvinculando temporalmente `AMDI0005:00` de `amd_pmc` antes
de entrar en s2idle y vinculándolo nuevamente al salir. Los reportes HID usados
por Windows también fueron identificados y probados. Este conocimiento todavía
debe integrarse formalmente al producto y al paquete.

## Fase 1: instalación base reproducible

Preparar una instalación desatendida de Fedora Workstation 44 que proporcione:

- usuario gráfico `andres`;
- usuario técnico `codex-admin`;
- acceso SSH desde el primer arranque;
- autologin gráfico para facilitar las transiciones de GPU;
- dependencias necesarias para construir, instalar y ejecutar G-Helper;
- configuración inicial suficientemente estable para repetir las pruebas.

Antes de instalar se debe comprobar la identidad exacta del disco interno de la
X13. La instalación debe abortar si no coincide, para evitar borrar otro disco.

### Criterio de salida

Fedora arranca en la X13, inicia la sesión de Andrés automáticamente y acepta
SSH mediante `codex-admin` sin intervención adicional.

## Fase 2: restaurar la línea base funcional

Instalar el MVP actual sin modificar su comportamiento y comprobar:

- lectura de sensores;
- perfiles de rendimiento;
- ventiladores;
- batería y límite de carga;
- teclado e iluminación;
- GPU integrada y GTX 1650;
- detección física de la XG Mobile;
- ciclo activar, desactivar, retirar y reconectar la RTX 3080.

Guardar inventario, logs y estado del hardware como referencia antes de aplicar
nuevos cambios.

### Criterio de salida

El comportamiento ya demostrado por el MVP se reproduce en la instalación
limpia y el estado de cada GPU coincide entre la GUI, sysfs, PCI y NVIDIA.

## Fase 3: integrar suspensión y reanudación con XG Mobile

Convertir el workaround experimental en una parte mantenible del producto:

1. Ejecutarlo solamente en el modelo GV301QH exacto.
2. Activarlo solamente cuando `egpu_enable=1`.
3. En `pre/suspend`, enviar el reporte HID correspondiente y desvincular
   `AMDI0005:00` de `amd_pmc`.
4. En `post/resume`, volver a vincular `amd_pmc`, restaurar la comunicación HID
   y verificar el estado de la RTX.
5. Hacer que la restauración sea idempotente y segura ante ejecuciones
   parciales, errores o múltiples ciclos.
6. Dejar logs estructurados que indiquen cada transición y su resultado.
7. Instalar y retirar los hooks junto con el paquete, sin pasos manuales.

El bloqueo está asociado al handoff profundo de `amd_pmc`/SMU cuando la XG está
activa. El workaround preserva la sesión y el dispositivo PCIe, pero no alcanza
el estado profundo S0i3. Ese costo energético debe mostrarse y documentarse.

### Criterio de salida

La misma sesión GNOME sobrevive a varios cierres y aperturas de tapa con la RTX
3080 activa. Después de cada ciclo, `amd_pmc` vuelve a estar vinculado, la RTX
responde y no quedan tareas bloqueadas en estado `D`.

## Fase 4: eliminar el workaround global de PCIe

El MVP actual exige `pcie_port_pm=off`. Los experimentos posteriores indican
que el hot-switch puede funcionar manteniendo en `on` solamente el root port de
la XG Mobile.

Se debe:

- identificar el root port por topología, no por una dirección PCI fija;
- aplicar la política puntual antes de una transición XG;
- restaurar el valor anterior cuando corresponda;
- probar arranque, hot-switch, suspensión y reanudación sin el parámetro global;
- conservar `pcie_port_pm=off` únicamente como fallback explícito si la política
  acotada no resulta estable.

### Criterio de salida

Todas las transiciones de la matriz física pasan sin el argumento global de
GRUB. Si no pasan, la release mantiene el fallback documentado y reversible.

## Fase 5: completar el estado y feedback de la GUI

La interfaz debe representar el hardware real mediante una máquina de estados:

- XG ausente;
- conectada pero desactivada;
- activando;
- activa;
- desactivando;
- bloqueada por procesos;
- error recuperable.

La GUI debe consultar el estado final al daemon después de cada operación y
actualizar el botón según ese resultado, no según la acción solicitada. También
debe:

- mostrar progreso durante descarga de módulos, rescan PCI y carga de NVIDIA;
- enumerar los procesos que retienen `/dev/nvidia*`;
- indicar cuándo es seguro retirar físicamente la XG;
- detectar una reconexión física;
- ofrecer logs legibles de la última transición;
- recuperarse si la GUI se cierra o reinicia durante una operación.

### Criterio de salida

El texto, el botón y el estado reportado siempre coinciden con sysfs, PCI y
NVIDIA después de una transición exitosa o fallida.

## Fase 6: recuperar las funciones normales de G-Helper

Auditar cada panel contra el hardware real y clasificarlo como soportado,
parcial o no soportado. La versión final debe habilitar al menos:

- perfiles de rendimiento y energía;
- lectura de temperaturas y RPM;
- curvas de ventiladores cuando el backend lo permita;
- límite de carga de batería;
- teclado e iluminación ASUS;
- pantalla, brillo y frecuencia;
- selección y estado de GPU;
- control completo de la XG Mobile validada.

Una función soportada no debe permanecer desactivada por los antiguos modos de
POC. Una función no soportada debe ocultarse o explicar concretamente qué falta;
no debe presentar un control que aparentemente funciona pero no hace nada.

### Criterio de salida

Cada control visible produce un efecto comprobable o devuelve una explicación
concreta. No quedan botones permanentemente deshabilitados ni estados ficticios.

## Fase 7: empaquetado final para Fedora

Crear un RPM que sea dueño de todos los componentes del producto:

- GUI y recursos;
- `ghelperd`;
- unidad systemd;
- configuración D-Bus;
- política polkit;
- reglas udev;
- integración con GNOME/autostart;
- hooks de suspensión y reanudación;
- configuración puntual de PCIe si queda validada;
- herramienta de diagnóstico y logs.

La instalación y desinstalación deben ser idempotentes. El desinstalador debe
retirar únicamente los cambios que pertenecen al paquete y restaurar cualquier
valor del sistema que haya reemplazado.

### Criterio de salida

Una instalación limpia requiere un solo comando, no deja scripts dispersos y
puede revertirse sin borrar la configuración personal de G-Helper.

## Fase 8: matriz física de aceptación

Ejecutar y documentar como mínimo:

| Escenario | Resultado exigido |
|---|---|
| Arranque sin XG | Sesión y controles normales disponibles |
| XG conectada pero desactivada | GTX 1650 activa; XG reconocida como inactiva |
| Activación en caliente | RTX 3080 funcional y GUI actualizada |
| Desactivación en caliente | NVIDIA liberada y retiro físico habilitado |
| Retiro físico seguro | Sin bloqueo ni estado fantasma |
| Reconexión física | XG detectada sin reiniciar la GUI |
| Suspensión sin XG | Reanudación normal |
| Suspensión con XG inactiva | Reanudación normal |
| Suspensión con XG activa | Misma sesión GNOME y RTX saludable |
| Ciclos repetidos de tapa | Sin degradación acumulativa |
| Reinicio en cada estado | Arranque determinista y recuperable |
| Apagado en cada estado | Sin bloqueo durante shutdown |
| Proceso reteniendo NVIDIA | Operación rechazada con proceso identificado |
| Fallo o transición interrumpida | Estado recuperable y diagnóstico claro |

Cada caso debe registrar estado previo y posterior de WMI, PCI, módulos NVIDIA,
sesión gráfica, `amd_pmc` y journal.

## Definición de versión final

La versión se considerará final cuando, desde una Fedora limpia:

1. se instale con un único procedimiento;
2. se inicie G-Helper automáticamente;
3. funcionen los controles soportados de la laptop;
4. la XG pueda activarse, desactivarse, retirarse y reconectarse desde la GUI;
5. cerrar y abrir la tapa con la RTX activa preserve la sesión;
6. ninguna operación normal requiera comandos manuales;
7. los fallos de hardware o procesos retenedores sean visibles y recuperables;
8. la instalación pueda revertirse sin dejar cambios opacos en el sistema.

## Trabajo posterior al cierre funcional

Queda deliberadamente fuera de la primera versión final:

- firma y repositorio de distribución del RPM;
- hardening adicional del daemon y de la cadena de actualización;
- soporte formal para otras versiones de Fedora;
- Ubuntu, Arch Linux, openSUSE y otras distribuciones;
- otros modelos Flow y otras generaciones de XG Mobile;
- optimización energética para alcanzar S0i3 profundo con la XG activa;
- upstreaming de los cambios aceptables al kernel y a proyectos ASUS/Linux.

Estos puntos no deben bloquear la entrega funcional para la máquina validada.

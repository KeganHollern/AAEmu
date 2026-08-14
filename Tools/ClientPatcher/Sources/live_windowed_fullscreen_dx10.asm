.386
.model flat
option casemap:none

; ArcheAge 1.2.4.13 r208022 / CryRenderD3D10.dll
;
; Captures r_FullscreenWindow alongside the renderer's exclusive-fullscreen
; state when selecting the Win32 window style. The setting itself is marked as
; restart-required in CrySystem, so this path is normally applied during the
; next client launch rather than while a decorated window is already active.

CAVE_BASE                  equ 382D2C40h
CAPTURE_MODE_CONTINUE      equ 380F6574h
GLOBAL_ENV                 equ 383AA9E0h

.code

capture_mode_stub proc
    mov dl, byte ptr [ebp+18h]
    cmp dword ptr ds:[GLOBAL_ENV], 0
    jz short capture_fullscreen_only
    mov ecx, dword ptr ds:[GLOBAL_ENV]
    cmp dword ptr [ecx+1Ch], 0
    jz short capture_fullscreen_only
    test eax, eax
    setne al
    or al, dl
    jmp short capture_store

capture_fullscreen_only:
    mov al, dl

capture_store:
    mov byte ptr [ebp-2], al
    mov ecx, dword ptr [ebx+0FB70h]
    db 0E9h
    dd CAPTURE_MODE_CONTINUE - (CAVE_BASE + ($ - capture_mode_stub) + 4)
capture_mode_stub endp

end

import type { useConfirm } from 'primevue/useconfirm'

type Confirm = ReturnType<typeof useConfirm>

/** Delete confirmation (CONTRACT §10.4): header "Silme Onayı", accept "Evet", reject "Hayır". */
export function confirmDelete(confirm: Confirm, message: string, onAccept: () => void | Promise<void>) {
  confirm.require({
    header: 'Silme Onayı',
    message,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Evet',
    rejectLabel: 'Hayır',
    acceptProps: { severity: 'danger', label: 'Evet' },
    rejectProps: { severity: 'secondary', outlined: true, label: 'Hayır' },
    accept: () => void onAccept(),
  })
}

export function confirmAction(
  confirm: Confirm,
  header: string,
  message: string,
  onAccept: () => void | Promise<void>,
) {
  confirm.require({
    header,
    message,
    icon: 'pi pi-question-circle',
    acceptLabel: 'Evet',
    rejectLabel: 'Hayır',
    acceptProps: { label: 'Evet' },
    rejectProps: { severity: 'secondary', outlined: true, label: 'Hayır' },
    accept: () => void onAccept(),
  })
}

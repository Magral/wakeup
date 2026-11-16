package ru.wakemini.app;

/** Shared visual state: a sent command is never presented as verified PC state. */
final class WidgetButtonStyle {
    final String label;
    final int color;
    final boolean busy;
    private WidgetButtonStyle(String label, int color, boolean busy) {
        this.label = label; this.color = color; this.busy = busy;
    }
    static int sizeDp(int width, int height) {
        int shortest = Math.min(width > 0 ? width : 56, height > 0 ? height : 56);
        return Math.max(40, Math.min(96, shortest));
    }
    static WidgetButtonStyle from(String status) {
        if (status.startsWith("Проверяю")) return new WidgetButtonStyle("Проверка", 0xffffc76e, true);
        if (status.startsWith("Команда выключения принята")) return new WidgetButtonStyle("Выключаю", 0xffffc76e, false);
        if (status.startsWith("Пакет пробуждения отправлен") || status.startsWith("ПК не ответил"))
            return new WidgetButtonStyle("Включаю", 0xffffc76e, false);
        if (status.startsWith("ПК включён")) return new WidgetButtonStyle("Выключить", 0xff50dfa9, false);
        if (status.startsWith("Ошибка")) return new WidgetButtonStyle("Ошибка", 0xffff818b, false);
        if (status.startsWith("Настройте")) return new WidgetButtonStyle("Настройка", 0xffffc76e, false);
        if (status.startsWith("ПК выключен")) return new WidgetButtonStyle("Нет связи", 0xffff818b, false);
        return new WidgetButtonStyle("Компьютер", 0xffb9d6df, false);
    }
}

package ru.wakemini.app;
public class WidgetButtonTest {
    public static void main(String[] args) {
        if (WidgetButtonStyle.sizeDp(160, 240) != 96) throw new AssertionError("Large rectangle must not make a giant button");
        if (WidgetButtonStyle.sizeDp(56, 90) != 56) throw new AssertionError("Compact cell must fit");
        if (WidgetButtonStyle.sizeDp(0, 0) != 56) throw new AssertionError("Launcher without size options needs safe fallback");
        if (!WidgetButtonStyle.from("Проверяю компьютер…").busy) throw new AssertionError("No busy feedback");
        if (!WidgetButtonStyle.from("Команда выключения принята. ПК выключится через 10 секунд.").label.equals("Выключаю")) throw new AssertionError("Shutdown ACK is not final offline state");
        if (!WidgetButtonStyle.from("ПК не ответил. Отправлен пакет включения.").label.equals("Включаю")) throw new AssertionError("Wake packet is not final online state");
        if (!WidgetButtonStyle.from("Ошибка: ключ не совпадает.").label.equals("Ошибка")) throw new AssertionError("Error state missing");
        if (WidgetButtonStyle.from("ПК включён · 60 мс").color != 0xff50dfa9) throw new AssertionError("Verified online status must be green");
        System.out.println("Widget sizing and truthful command feedback passed.");
    }
}

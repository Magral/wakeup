package ru.wakemini.app;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity {
    private static final int BG = Color.rgb(16, 24, 32);
    private static final int PANEL = Color.rgb(25, 36, 46);
    private static final int TEXT = Color.rgb(240, 245, 248);
    private static final int MUTED = Color.rgb(157, 173, 184);
    private static final int ACCENT = Color.rgb(39, 198, 183);
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private EditText macInput;
    private EditText addressInput;
    private EditText portInput;
    private EditText keyInput;
    private Button toggleButton;
    private TextView statusView;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        buildScreen();
        loadSaved();
    }

    private void buildScreen() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BG);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(22), dp(24), dp(22), dp(22));
        scroll.addView(page);

        TextView eyebrow = text("WAKE MINI", 12, ACCENT, true);
        eyebrow.setLetterSpacing(0.12f);
        page.addView(eyebrow);
        TextView title = text("Управление ПК", 30, TEXT, true);
        LinearLayout.LayoutParams titleParams = params(-1, -2);
        titleParams.topMargin = dp(7);
        page.addView(title, titleParams);
        TextView intro = text("Нажмите кнопку: включить или выключить компьютер.", 15, MUTED, false);
        LinearLayout.LayoutParams introParams = params(-1, -2);
        introParams.topMargin = dp(6);
        page.addView(intro, introParams);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(17), dp(16), dp(17));
        card.setBackground(round(PANEL, dp(18)));
        LinearLayout.LayoutParams cardParams = params(-1, -2);
        cardParams.topMargin = dp(25);
        page.addView(card, cardParams);

        addLabel(card, "MAC-АДРЕС ПК");
        macInput = input("Например, 00:11:22:AA:BB:CC", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        addInput(card, macInput);

        addLabel(card, "АДРЕС НАЗНАЧЕНИЯ / DDNS", dp(16));
        addressInput = input("Например, magral.tplinkdns.com", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        addInput(card, addressInput);

        addLabel(card, "UDP-ПОРТ", dp(16));
        portInput = input("3000", InputType.TYPE_CLASS_NUMBER);
        addInput(card, portInput);

        addLabel(card, "КЛЮЧ ПОМОЩНИКА WINDOWS", dp(16));
        keyInput = input("Вставьте ключ из WakeMiniAgent.exe", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        addInput(card, keyInput);
        TextView keyHint = text("Ключ нужен для выключения. Без него кнопка сможет только разбудить ПК.", 12, MUTED, false);
        keyHint.setPadding(dp(2), dp(8), dp(2), 0);
        card.addView(keyHint);

        toggleButton = new Button(this);
        toggleButton.setText("⏻   ВКЛ / ВЫКЛ ПК");
        toggleButton.setTextColor(Color.rgb(9, 28, 30));
        toggleButton.setTextSize(15);
        toggleButton.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        toggleButton.setAllCaps(false);
        toggleButton.setBackground(round(ACCENT, dp(16)));
        toggleButton.setPadding(dp(12), 0, dp(12), 0);
        LinearLayout.LayoutParams buttonParams = params(-1, dp(58));
        buttonParams.topMargin = dp(20);
        page.addView(toggleButton, buttonParams);
        toggleButton.setOnClickListener(v -> saveAndToggle());

        statusView = text("Готово", 14, MUTED, false);
        statusView.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams statusParams = params(-1, -2);
        statusParams.topMargin = dp(15);
        page.addView(statusView, statusParams);

        TextView setup = text("Для выключения запустите WakeMiniAgent.exe на ПК и введите показанный им ключ. На Archer AX1800 перенаправьте UDP-порт на адрес ПК и разрешите этот порт в Брандмауэре Windows. Потом добавьте виджет Wake Mini на главный экран.", 13, MUTED, false);
        setup.setLineSpacing(dp(3), 1f);
        setup.setPadding(dp(2), dp(18), dp(2), dp(4));
        page.addView(setup);
        setContentView(scroll);
    }

    private void addInput(LinearLayout parent, EditText input) {
        LinearLayout.LayoutParams lp = params(-1, dp(54));
        lp.topMargin = dp(8);
        parent.addView(input, lp);
    }

    private void addLabel(LinearLayout parent, String value) { addLabel(parent, value, 0); }
    private void addLabel(LinearLayout parent, String value, int top) {
        TextView label = text(value, 11, MUTED, true);
        label.setLetterSpacing(0.08f);
        LinearLayout.LayoutParams lp = params(-1, -2);
        lp.topMargin = dp(top);
        parent.addView(label, lp);
    }

    private EditText input(String hint, int type) {
        EditText e = new EditText(this);
        e.setSingleLine(true);
        e.setTextSize(15);
        e.setTextColor(TEXT);
        e.setHintTextColor(Color.rgb(111, 129, 141));
        e.setHint(hint);
        e.setInputType(type);
        e.setPadding(dp(14), 0, dp(14), 0);
        e.setBackground(round(Color.rgb(16, 24, 32), dp(12)));
        return e;
    }

    private TextView text(String value, int size, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(value);
        t.setTextSize(size);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return t;
    }

    private GradientDrawable round(int color, int radius) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(radius);
        return d;
    }

    private LinearLayout.LayoutParams params(int width, int height) { return new LinearLayout.LayoutParams(width, height); }
    private int dp(float value) { return (int) (value * getResources().getDisplayMetrics().density + 0.5f); }

    private void loadSaved() {
        android.content.SharedPreferences p = getSharedPreferences(WakeController.PREFS, MODE_PRIVATE);
        macInput.setText(p.getString("mac", ""));
        addressInput.setText(p.getString("address", p.getString("broadcast", "255.255.255.255")));
        portInput.setText(p.getString("port", "3000"));
        keyInput.setText(p.getString("key", ""));
    }

    private void saveAndToggle() {
        String mac = macInput.getText().toString().trim();
        String address = addressInput.getText().toString().trim();
        String port = portInput.getText().toString().trim();
        String key = keyInput.getText().toString().trim();
        String issue = WakeController.validate(mac, address, port, key);
        if (issue != null) {
            statusView.setText(issue);
            statusView.setTextColor(Color.rgb(255, 133, 133));
            return;
        }
        getSharedPreferences(WakeController.PREFS, MODE_PRIVATE).edit()
                .putString("mac", mac).putString("address", address).putString("port", port).putString("key", key).apply();
        ((InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(toggleButton.getWindowToken(), 0);
        setBusy(true, "Проверяю компьютер…");
        worker.execute(() -> {
            String result = WakeController.toggle(this);
            runOnUiThread(() -> setBusy(false, result));
        });
    }

    private void setBusy(boolean busy, String message) {
        toggleButton.setEnabled(!busy);
        toggleButton.setAlpha(busy ? 0.6f : 1f);
        statusView.setText(message);
        statusView.setTextColor(message.startsWith("Ошибка") ? Color.rgb(255, 133, 133) : (busy ? ACCENT : MUTED));
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }
}

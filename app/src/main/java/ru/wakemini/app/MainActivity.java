package ru.wakemini.app;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import org.json.JSONObject;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
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
    private final ExecutorService statusWorker = Executors.newSingleThreadExecutor();
    private EditText macInput;
    private EditText addressInput;
    private EditText portInput;
    private EditText keyInput;
    private Button toggleButton;
    private TextView statusView;
    private View connectionDot;
    private TextView connectionView;
    private boolean statusCheckRunning;
    private final Handler statusHandler = new Handler(Looper.getMainLooper());
    private final Runnable statusPoll = new Runnable() {
        @Override public void run() {
            refreshPcStatus();
            statusHandler.postDelayed(this, 15_000);
        }
    };

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        buildScreen();
        loadSaved();
        enableAutoSave();
    }

    private void buildScreen() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BG);
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(22), dp(24), dp(22), dp(22));
        scroll.addView(page);

        TextView eyebrow = text("WAKE MINI 1.3.0", 12, ACCENT, true);
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

        LinearLayout connectionPanel = new LinearLayout(this);
        connectionPanel.setOrientation(LinearLayout.HORIZONTAL);
        connectionPanel.setGravity(Gravity.CENTER_VERTICAL);
        connectionPanel.setPadding(dp(14), dp(12), dp(14), dp(12));
        connectionPanel.setBackground(round(PANEL, dp(14)));
        LinearLayout.LayoutParams connectionParams = params(-1, -2);
        connectionParams.topMargin = dp(18);
        page.addView(connectionPanel, connectionParams);

        connectionDot = new View(this);
        setIndicatorColor(Color.rgb(130, 145, 155));
        LinearLayout.LayoutParams dotParams = params(dp(14), dp(14));
        dotParams.rightMargin = dp(10);
        connectionPanel.addView(connectionDot, dotParams);

        connectionView = text("Проверяю связь с ПК…", 14, MUTED, true);
        LinearLayout.LayoutParams connectionTextParams = new LinearLayout.LayoutParams(0, -2, 1f);
        connectionPanel.addView(connectionView, connectionTextParams);

        TextView refreshStatus = text("ПРОВЕРИТЬ", 11, ACCENT, true);
        refreshStatus.setPadding(dp(8), dp(8), 0, dp(8));
        refreshStatus.setOnClickListener(v -> refreshPcStatus());
        connectionPanel.addView(refreshStatus);

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
        TextView saveHint = text("Настройки сохраняются автоматически на этом телефоне.", 12, MUTED, false);
        saveHint.setPadding(dp(2), dp(8), dp(2), 0);
        card.addView(saveHint);

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
        Button scan = new Button(this);
        scan.setText("Настроить по QR-коду с ПК");
        scan.setOnClickListener(v -> new IntentIntegrator(this).setDesiredBarcodeFormats(IntentIntegrator.QR_CODE)
                .setPrompt("Отсканируйте QR в помощнике Wake Mini на ПК").setBeepEnabled(false).initiateScan());
        page.addView(scan, params(-1, -2));
        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.VERTICAL);
        page.addView(actions);
        addAction(actions, "Включить ПК", "ON", false);
        addAction(actions, "Выключить ПК", "SHUTDOWN", true);
        addAction(actions, "Работа", "PROFILE_WORK", false);
        addAction(actions, "Игры", "PROFILE_GAME", false);
        addAction(actions, "Кино", "PROFILE_CINEMA", false);
        Button diagnostic = new Button(this);
        diagnostic.setText("Диагностика и история команд");
        diagnostic.setOnClickListener(v -> new AlertDialog.Builder(this).setTitle("Диагностика")
                .setMessage(connectionView.getText() + "\n\n" + getSharedPreferences(WakeController.PREFS, MODE_PRIVATE).getString("history", "Команд ещё нет."))
                .setPositiveButton("Закрыть", null).show());
        page.addView(diagnostic, params(-1, -2));
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

    @Override
    protected void onResume() {
        super.onResume();
        statusHandler.removeCallbacks(statusPoll);
        refreshPcStatus();
        statusHandler.postDelayed(statusPoll, 15_000);
    }

    @Override
    protected void onPause() {
        statusHandler.removeCallbacks(statusPoll);
        super.onPause();
    }

    private void setIndicatorColor(int color) {
        if (connectionDot == null) return;
        GradientDrawable dot = new GradientDrawable();
        dot.setShape(GradientDrawable.OVAL);
        dot.setColor(color);
        connectionDot.setBackground(dot);
    }

    private void setPcStatus(String message, int color) {
        if (connectionView == null) return;
        connectionView.setText(message);
        connectionView.setTextColor(color);
        setIndicatorColor(color);
    }

    private void refreshPcStatus() {
        if (connectionView == null || statusCheckRunning) return;
        String mac = macInput.getText().toString().trim();
        String address = addressInput.getText().toString().trim();
        String port = portInput.getText().toString().trim();
        String key = keyInput.getText().toString().trim();
        String issue = WakeController.validate(mac, address, port, key);
        if (issue != null || key.isEmpty()) {
            setPcStatus("Заполните настройки и ключ помощника", Color.rgb(130, 145, 155));
            return;
        }

        statusCheckRunning = true;
        setPcStatus("Проверяю связь…", ACCENT);
        statusWorker.execute(() -> {
            WakeController.Status result = WakeController.diagnose(this);
            runOnUiThread(() -> {
                statusCheckRunning = false;
                if (isFinishing() || isDestroyed()) return;
                setPcStatus(result.message, result.online ? Color.rgb(74, 214, 132) : Color.rgb(255, 105, 105));
            });
        });
    }

    private void loadSaved() {
        android.content.SharedPreferences p = getSharedPreferences(WakeController.PREFS, MODE_PRIVATE);
        macInput.setText(p.getString("mac", ""));
        addressInput.setText(p.getString("address", p.getString("broadcast", "255.255.255.255")));
        portInput.setText(p.getString("port", "3000"));
        keyInput.setText(p.getString("key", ""));
    }

    private void enableAutoSave() {
        TextWatcher watcher = new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override public void afterTextChanged(Editable s) { saveSettings(); }
        };
        macInput.addTextChangedListener(watcher);
        addressInput.addTextChangedListener(watcher);
        portInput.addTextChangedListener(watcher);
        keyInput.addTextChangedListener(watcher);
    }

    private void saveSettings() {
        if (macInput == null || addressInput == null || portInput == null || keyInput == null) return;
        saveSettings(
                macInput.getText().toString().trim(),
                addressInput.getText().toString().trim(),
                portInput.getText().toString().trim(),
                keyInput.getText().toString().trim());
    }

    private void saveSettings(String mac, String address, String port, String key) {
        getSharedPreferences(WakeController.PREFS, MODE_PRIVATE).edit()
                .putString("mac", mac)
                .putString("address", address)
                .putString("port", port)
                .putString("key", key)
                .apply();
    }

    private void saveAndToggle() {
        if (actionRunning) return;
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
        saveSettings(mac, address, port, key);
        ((InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(toggleButton.getWindowToken(), 0);
        actionRunning = true;
        setBusy(true, "Проверяю компьютер…");
        worker.execute(() -> {
            String result = WakeController.toggle(this);
            rememberResult("TOGGLE", result);
            runOnUiThread(() -> { actionRunning = false; if (!isDestroyed()) { setBusy(false, result); refreshPcStatus(); } });
        });
    }

    private void addAction(LinearLayout parent, String title, String action, boolean confirm) {
        Button button = new Button(this);
        button.setText(title);
        parent.addView(button, params(-1, -2));
        button.setOnClickListener(v -> {
            if (confirm) new AlertDialog.Builder(this).setTitle("Выключить компьютер?")
                    .setMessage("Сохраните открытые документы. Windows выключится через 10 секунд.")
                    .setNegativeButton("Отмена", null).setPositiveButton("Выключить", (d, which) -> executeAction(action)).show();
            else executeAction(action);
        });
    }

    private boolean actionRunning;
    private void executeAction(String action) {
        if (actionRunning) return;
        actionRunning = true;
        setBusy(true, "Отправляю команду…");
        worker.execute(() -> {
            String result = WakeController.execute(this, action);
            rememberResult(action, result);
            runOnUiThread(() -> {
                actionRunning = false;
                if (!isDestroyed()) { setBusy(false, result); refreshPcStatus(); }
            });
        });
    }

    private void rememberResult(String action, String result) {
        android.content.SharedPreferences p = getSharedPreferences(WakeController.PREFS, MODE_PRIVATE);
        String entry = new java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(new java.util.Date()) + " " + action + ": " + result;
        String history = entry + "\n" + p.getString("history", "");
        p.edit().putString("history", history.substring(0, Math.min(5000, history.length()))).apply();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        IntentResult scan = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (scan == null) { super.onActivityResult(requestCode, resultCode, data); return; }
        if (scan.getContents() == null) return;
        try {
            if (scan.getContents().length() > 4096) throw new IllegalArgumentException();
            JSONObject cfg = new JSONObject(scan.getContents());
            if (!cfg.getString("type").equals("wakemini") || cfg.getInt("version") != 1) throw new IllegalArgumentException();
            String mac = cfg.getString("mac"), host = cfg.getString("host"), port = cfg.getString("port"), key = cfg.getString("key");
            if (key.length() != 64 || WakeController.validate(mac, host, port, key) != null) throw new IllegalArgumentException();
            new AlertDialog.Builder(this).setTitle("Подключить компьютер?")
                    .setMessage("Адрес: " + host + "\nMAC: " + mac + "\nСуществующие настройки будут заменены.")
                    .setNegativeButton("Отмена", null).setPositiveButton("Подключить", (d, which) -> {
                        macInput.setText(mac); addressInput.setText(host); portInput.setText(port); keyInput.setText(key);
                        saveSettings(); refreshPcStatus();
                    }).show();
        } catch (Exception ex) { new AlertDialog.Builder(this).setMessage("Это не корректный QR-код Wake Mini.").setPositiveButton("OK", null).show(); }
    }

    private void setBusy(boolean busy, String message) {
        toggleButton.setEnabled(!busy);
        toggleButton.setAlpha(busy ? 0.6f : 1f);
        statusView.setText(message);
        statusView.setTextColor(message.startsWith("Ошибка") ? Color.rgb(255, 133, 133) : (busy ? ACCENT : MUTED));
    }

    @Override
    protected void onDestroy() {
        statusHandler.removeCallbacks(statusPoll);
        statusWorker.shutdownNow();
        worker.shutdownNow();
        super.onDestroy();
    }
}

"""ISBC 보드 가상 테스트 창: 버튼 하나로 Renode(부트로더 + 팀 앱) + USART1 + TCP + 3D 보드 화면을 연다."""

import json
import sys
from datetime import datetime
from pathlib import Path

from PySide6.QtWebEngineWidgets import QWebEngineView  # QApplication보다 먼저 불러야 한다.
from PySide6.QtCore import QSettings, Qt, QTimer, QUrl
from PySide6.QtGui import QDesktopServices, QFont
from PySide6.QtNetwork import (QAbstractSocket, QNetworkAccessManager, QNetworkProxy, QNetworkReply,
                               QNetworkRequest)
from PySide6.QtWidgets import (QApplication, QCheckBox, QFileDialog, QFrame, QGridLayout, QHBoxLayout,
                               QLabel, QLineEdit, QMainWindow, QPlainTextEdit, QPushButton, QSplitter,
                               QVBoxLayout, QWidget)

from command_wire import ResponseStream
from runtime import RenodeLauncher
from tcp_command_client import TcpCommandClient


HOST = '127.0.0.1'
COMMAND_PORT = 18000   # 펌웨어 TCP 8000
WEB_PORT = 18080       # 펌웨어 웹 80
BOARD_PORT = 18100     # 3D 보드 화면
BOARD_URL = f'http://{HOST}:{BOARD_PORT}/'

# 기본 앱 ELF: 패키지에 함께 넣은 예시 ELF.
def default_app_elf():
    candidates = [Path(__file__).resolve().parent.parent.parent / 'apps' / 'Vial_Decapper.elf']
    for path in candidates:
        if path.is_file():
            return path.as_posix()
    return ''


DEFAULT_APP_ELF = default_app_elf()

STYLE = '''
QMainWindow, QWidget#simRoot { background: #0b1622; color: #dce8f0; }
QWidget { color: #dce8f0; font-size: 13px; }
QFrame[panel="true"] { background: #12202e; border: 1px solid #263949; border-radius: 12px; }
QLabel#brand { color: #69d9cc; font-size: 10px; font-weight: 700; }
QLabel#title { font-size: 28px; font-weight: 650; color: #f1f7fb; }
QLabel[secondary="true"] { color: #9fb3c3; font-size: 12px; }
QLabel[section="true"] { color: #e6f0f7; font-size: 15px; font-weight: 650; }
QLabel[metric="true"] { color: #8fa9bd; font-size: 12px; }
QLabel[value="true"] { color: #e7f3fb; font-size: 13px; font-weight: 600; }
QLabel[badge="true"] { color: #adbfcc; background: #233444; border: 1px solid #31485b;
                      padding: 4px 9px; border-radius: 7px; font-size: 12px; }
QLabel[badge="true"][tone="good"] { color: #a9f1e2; background: #123832; border-color: #265548; }
QLabel[badge="true"][tone="wait"] { color: #f4d5a4; background: #3b2c1d; border-color: #5b4630; }
QPushButton { background: #203244; color: #e5f0f6; border: 1px solid #314b60;
              border-radius: 7px; padding: 5px 10px; min-height: 22px; }
QPushButton:hover { background: #2a435a; border-color: #48728c; }
QPushButton:pressed { background: #172737; }
QPushButton[primary="true"] { background: #76dfd0; color: #0c242b; border-color: #8ae7da; font-weight: 700; }
QPushButton[primary="true"]:hover { background: #a2eee3; }
QPushButton:disabled, QPushButton[primary="true"]:disabled { background: #1b2835; color: #728594;
                                                           border-color: #253847; }
QLineEdit, QPlainTextEdit { color: #dce8f0; background: #0b1723; border: 1px solid #304859;
                            border-radius: 7px; padding: 6px; }
QLineEdit:focus { border-color: #74d5c9; }
QLineEdit:disabled { color: #758a9a; }
QCheckBox { color: #c5d6e2; spacing: 6px; }
QSplitter::handle { background: transparent; }
'''

WAITING_HTML = '''<html><body style="margin:0;background:#0b1723;color:#9fb3c3;font:14px 'Malgun Gothic';
display:flex;align-items:center;justify-content:center;height:100vh">
<div style="text-align:center">3D 보드 화면 연결 대기<br><br>
<span style="font-size:12px">위의 <b>Renode + UART 열기</b>를 누르면 펌웨어 부팅 후 여기에 보드가 나타납니다.</span></div>
</body></html>'''


def label(text='', role=None):
    result = QLabel(text)
    result.setTextFormat(Qt.TextFormat.PlainText)
    if role:
        result.setProperty(role, True)
    return result


def panel():
    frame = QFrame()
    frame.setProperty('panel', True)
    layout = QVBoxLayout(frame)
    layout.setContentsMargins(12, 11, 12, 11)
    layout.setSpacing(8)
    return frame, layout


def set_badge(widget, text, tone='neutral'):
    if widget.text() != text:
        widget.setText(text)
    if widget.property('tone') != tone:
        widget.setProperty('tone', tone)
        widget.style().unpolish(widget)
        widget.style().polish(widget)


class BoardWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.settings = QSettings('RND', 'ISBC Renode Test')
        self.board_online = False
        self.page_loaded = False
        self.setWindowTitle('ISBC 보드 · Renode Test')
        self.resize(1180, 780)
        self.setMinimumSize(920, 660)
        self.setStyleSheet(STYLE)
        self._build_ui()

        self.client = TcpCommandClient(self)
        self.stream = ResponseStream(lambda: self.append_log('RX', '응답 길이 제한을 초과했습니다.'))
        self.client.connected.connect(lambda: self.append_log('TCP', f'{HOST}:{COMMAND_PORT} 연결됨'))
        self.client.disconnected.connect(self._tcp_disconnected)
        self.client.error.connect(lambda error: self.append_log('TCP', error))
        self.client.received.connect(self._tcp_received)
        self.client.sent.connect(lambda data: self.append_log('TX', data.decode('ascii').rstrip('\r\n')))
        self.client.socket.stateChanged.connect(self._sync_tcp_widgets)
        self._sync_tcp_widgets()

        self.launcher = RenodeLauncher(HOST, {COMMAND_PORT: 'TCP 명령', WEB_PORT: '펌웨어 웹',
                                             BOARD_PORT: '3D 보드 화면'}, self)
        self.launcher.finished.connect(self._launch_finished)

        self.network = QNetworkAccessManager(self)
        self.network.setProxy(QNetworkProxy(QNetworkProxy.ProxyType.NoProxy))
        self.poll_reply = None
        self.poll = QTimer(self)
        self.poll.setInterval(800)
        self.poll.timeout.connect(self._poll_board)
        self.poll.start()
        self._poll_board()

    # ---- 화면 구성 ----
    def _build_ui(self):
        root = QWidget()
        root.setObjectName('simRoot')
        self.setCentralWidget(root)
        layout = QVBoxLayout(root)
        layout.setContentsMargins(18, 16, 18, 14)
        layout.setSpacing(12)

        header = QHBoxLayout()
        header.setSpacing(10)
        titles = QVBoxLayout()
        titles.setSpacing(2)
        brand = label('RENODE  /  ISBC BOARD TEST BENCH')
        brand.setObjectName('brand')
        titles.addWidget(brand)
        title = label('ISBC 보드')
        title.setObjectName('title')
        titles.addWidget(title)
        titles.addWidget(label('부트로더 위에 팀 앱 펌웨어를 올려 실행하는 가상 보드', 'secondary'))
        header.addLayout(titles, 1)
        self.run_button = QPushButton('Renode + UART 열기')
        self.run_button.setProperty('primary', True)
        self.run_button.clicked.connect(self._start_renode)
        header.addWidget(self.run_button)
        web_button = QPushButton('웹 화면')
        web_button.setToolTip(f'펌웨어 웹 서버 http://{HOST}:{WEB_PORT}/')
        web_button.clicked.connect(lambda: QDesktopServices.openUrl(QUrl(f'http://{HOST}:{WEB_PORT}/')))
        header.addWidget(web_button)
        layout.addLayout(header)

        layout.addWidget(self._build_app_panel())

        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.setChildrenCollapsible(False)
        splitter.setHandleWidth(10)
        layout.addWidget(splitter, 1)

        board_panel, board_layout = panel()
        board_header = QHBoxLayout()
        board_header.addWidget(label('보드 3D', 'section'))
        board_header.addStretch()
        self.board_status = label('', 'badge')
        set_badge(self.board_status, '연결 대기', 'wait')
        board_header.addWidget(self.board_status)
        browser_button = QPushButton('브라우저로 열기')
        browser_button.clicked.connect(lambda: QDesktopServices.openUrl(QUrl(BOARD_URL)))
        board_header.addWidget(browser_button)
        board_layout.addLayout(board_header)
        self.board_view = QWebEngineView()
        self.board_view.setMinimumSize(420, 360)
        self.board_view.setHtml(WAITING_HTML)
        board_layout.addWidget(self.board_view, 1)
        splitter.addWidget(board_panel)

        right = QWidget()
        right.setMinimumWidth(372)
        right_layout = QVBoxLayout(right)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.setSpacing(10)
        right_layout.addWidget(self._build_state_panel())
        right_layout.addWidget(self._build_command_panel(), 1)
        splitter.addWidget(right)
        splitter.setSizes([700, 420])
        splitter.setStretchFactor(0, 3)
        splitter.setStretchFactor(1, 2)

        self.hint = label('UART 명령은 Renode의 USART1 창에서, TCP 명령은 오른쪽 창에서 입력하세요. 둘 다 같은 가상 보드입니다.',
                          'secondary')
        self.hint.setWordWrap(True)
        layout.addWidget(self.hint)

    def _build_app_panel(self):
        frame, layout = panel()
        row = QHBoxLayout()
        row.setSpacing(8)
        row.addWidget(label('앱 ELF', 'section'))
        saved = self.settings.value('appElf', '')
        self.app_input = QLineEdit(saved if saved and Path(saved).is_file() else DEFAULT_APP_ELF)
        self.app_input.setPlaceholderText('STM32CubeIDE Debug 빌드 ELF 경로')
        self.app_input.setToolTip('부트로더 위에 올릴 팀 앱. 코드 수정 후 CubeIDE에서 Debug 빌드하고 Renode를 다시 여세요.')
        row.addWidget(self.app_input, 1)
        self.browse_button = QPushButton('찾아보기')
        self.browse_button.clicked.connect(self._browse_app)
        row.addWidget(self.browse_button)
        self.boot_only = QCheckBox('부트로더만(앱 없음)')
        self.boot_only.setChecked(self.settings.value('bootOnly', False, type=bool))
        self.boot_only.toggled.connect(self._sync_app_widgets)
        row.addWidget(self.boot_only)
        layout.addLayout(row)
        self._sync_app_widgets()
        return frame

    def _build_state_panel(self):
        frame, layout = panel()
        layout.addWidget(label('보드 연결', 'section'))
        rows = QGridLayout()
        rows.setHorizontalSpacing(10)
        rows.setVerticalSpacing(6)
        for index, (title, value) in enumerate((('TCP 명령', f'{HOST}:{COMMAND_PORT}  (펌웨어 8000)'),
                                                ('펌웨어 웹', f'{HOST}:{WEB_PORT}  (펌웨어 80)'),
                                                ('3D 보드 화면', f'{HOST}:{BOARD_PORT}'),
                                                ('USART1', 'Renode의 USART1 창 (115200, CLI)'))):
            rows.addWidget(label(title, 'metric'), index, 0)
            rows.addWidget(label(value, 'value'), index, 1)
        rows.setColumnStretch(1, 1)
        layout.addLayout(rows)
        leds = QHBoxLayout()
        leds.setSpacing(5)
        self.led_badges = []
        for index in range(4):
            badge = label(f'LED{index} —', 'badge')
            badge.setAlignment(Qt.AlignmentFlag.AlignCenter)
            self.led_badges.append(badge)
            leds.addWidget(badge, 1)
        layout.addLayout(leds)
        return frame

    def _build_command_panel(self):
        frame, layout = panel()
        header = QHBoxLayout()
        header.addWidget(label('TCP 명령', 'section'))
        header.addStretch()
        self.tcp_status = label('연결 안 됨', 'badge')
        header.addWidget(self.tcp_status)
        self.connect_button = QPushButton('연결')
        self.connect_button.clicked.connect(self._toggle_tcp)
        header.addWidget(self.connect_button)
        layout.addLayout(header)
        layout.addWidget(label(f'{HOST}:{COMMAND_PORT}  ·  ASCII / CRLF  ·  명령은 올린 앱에 따라 다릅니다', 'secondary'))

        shortcuts = QHBoxLayout()
        self.command_buttons = []
        for command, hint in [('VER', '펌웨어 버전 조회'), ('VERSION', '펌웨어 버전 조회(긴 이름)')]:
            button = QPushButton(command)
            button.setToolTip(hint)
            button.clicked.connect(lambda _checked=False, command=command: self.send_command(command))
            self.command_buttons.append(button)
            shortcuts.addWidget(button)
        shortcuts.addStretch()
        clear_button = QPushButton('로그 지우기')
        shortcuts.addWidget(clear_button)
        layout.addLayout(shortcuts)

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMinimumHeight(98)
        self.log.setMaximumBlockCount(1500)
        self.log.setPlaceholderText('전송한 명령과 펌웨어의 실제 응답이 여기에 표시됩니다.')
        clear_button.clicked.connect(self.log.clear)
        layout.addWidget(self.log, 1)

        editor = QHBoxLayout()
        self.command_input = QLineEdit()
        self.command_input.setPlaceholderText('명령 입력 · 예: VER')
        self.command_input.returnPressed.connect(self._send_entry)
        editor.addWidget(self.command_input, 1)
        self.send_button = QPushButton('전송')
        self.send_button.setProperty('primary', True)
        self.send_button.clicked.connect(self._send_entry)
        editor.addWidget(self.send_button)
        layout.addLayout(editor)
        return frame

    # ---- 앱 선택 / Renode 실행 ----
    def _sync_app_widgets(self, _checked=None):
        enabled = not self.boot_only.isChecked()
        self.app_input.setEnabled(enabled)
        self.browse_button.setEnabled(enabled)

    def _browse_app(self):
        start = str(Path(self.app_input.text()).parent) if self.app_input.text() else ''
        path, _ = QFileDialog.getOpenFileName(self, '앱 ELF 선택', start, 'ELF (*.elf);;모든 파일 (*)')
        if path:
            self.app_input.setText(path)

    def _start_renode(self):
        if self.board_online:
            self.hint.setText('이미 실행 중인 Renode에 연결되어 있습니다. 다시 열려면 그 Renode를 먼저 닫으세요.')
            return
        app_elf = None if self.boot_only.isChecked() else self.app_input.text().strip()
        if not self.boot_only.isChecked() and not app_elf:
            self.hint.setText('앱 ELF 경로를 입력하거나 "부트로더만"을 선택하세요.')
            return
        self.settings.setValue('appElf', self.app_input.text().strip())
        self.settings.setValue('bootOnly', self.boot_only.isChecked())
        self.run_button.setEnabled(False)
        self.hint.setText('Renode를 열기 전에 로컬 포트를 확인하고 있습니다.')
        self.launcher.start(app_elf)

    def _launch_finished(self, started, message):
        self.hint.setText(message)
        if started:
            QTimer.singleShot(45000, self, lambda: self.run_button.setEnabled(True))
        else:
            self.run_button.setEnabled(True)

    # ---- 3D 보드 화면 상태 ----
    def _poll_board(self):
        if self.poll_reply is not None:
            return
        request = QNetworkRequest(QUrl(BOARD_URL + 'state'))
        request.setTransferTimeout(1500)
        self.poll_reply = self.network.get(request)
        self.poll_reply.finished.connect(self._board_reply)

    def _board_reply(self):
        reply, self.poll_reply = self.poll_reply, None
        try:
            if reply.error() != QNetworkReply.NetworkError.NoError:
                raise OSError(reply.errorString())
            state = json.loads(bytes(reply.readAll()).decode('utf-8'))
            self._show_board(state)
        except (OSError, ValueError) as error:
            self._board_offline(str(error))
        finally:
            reply.deleteLater()

    def _show_board(self, state):
        if not self.board_online:
            self.board_online = True
            if not self.page_loaded:
                self.board_view.load(QUrl(BOARD_URL))
                self.page_loaded = True
        running = bool(state.get('running'))
        set_badge(self.board_status, '실시간 연결' if running else 'Renode 일시정지', 'good' if running else 'wait')
        for badge, led in zip(self.led_badges, state.get('leds', [])):
            on = bool(led.get('on'))
            set_badge(badge, f'LED{led.get("id")} {"ON" if on else "OFF"}', 'good' if on else 'neutral')

    def _board_offline(self, error):
        self.board_status.setToolTip(error)
        if self.board_online:
            self.board_online = False
            set_badge(self.board_status, '연결 끊김', 'wait')
            # Renode를 다시 열면 새 페이지를 불러오도록 한다.
            self.page_loaded = False
            self.board_view.setHtml(WAITING_HTML)
            for index, badge in enumerate(self.led_badges):
                set_badge(badge, f'LED{index} —')

    # ---- TCP ----
    def _toggle_tcp(self):
        if self.client.socket.state() != QAbstractSocket.SocketState.UnconnectedState:
            self.client.disconnect_from_host()
        else:
            self.stream.reset()
            self.client.connect_to_host(HOST, COMMAND_PORT)

    def _sync_tcp_widgets(self, _state=None):
        state = self.client.socket.state()
        connected = state == QAbstractSocket.SocketState.ConnectedState
        pending = state != QAbstractSocket.SocketState.UnconnectedState and not connected
        set_badge(self.tcp_status, '연결됨' if connected else '연결 중' if pending else '연결 안 됨',
                  'good' if connected else 'wait' if pending else 'neutral')
        self.connect_button.setText('연결 끊기' if connected else '취소' if pending else '연결')
        for widget in (*self.command_buttons, self.command_input, self.send_button):
            widget.setEnabled(connected)

    def _tcp_disconnected(self):
        self.stream.reset()
        self.append_log('TCP', '연결 종료')

    def _tcp_received(self, data):
        for line in self.stream.feed(data):
            self.append_log('RX', line)

    def _send_entry(self):
        if self.send_command(self.command_input.text()):
            self.command_input.selectAll()

    def send_command(self, command):
        return self.client.send_command(command, append_cr=True, append_lf=True)

    def append_log(self, direction, line):
        self.log.appendPlainText(f'{datetime.now():%H:%M:%S}  {direction:<3}  {line}')

    def closeEvent(self, event):
        self.poll.stop()
        self.client.disconnect_from_host()
        super().closeEvent(event)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName('ISBC Renode Test')
    app.setFont(QFont('Malgun Gothic', 10))
    window = BoardWindow()
    window.show()
    return app.exec()


if __name__ == '__main__':
    raise SystemExit(main())

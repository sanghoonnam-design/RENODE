"""ISBC 보드용 Renode를 GUI 모드(Monitor + USART1 창)로 띄운다."""

from pathlib import Path
import socket

from PySide6.QtCore import QObject, QProcess, Signal

ISBC_DIR = Path(__file__).resolve().parent.parent
RENODE = ISBC_DIR.parent / 'renode_1.17.0-portable' / 'renode.exe'


def renode_commands(app_elf=None):
    """Renode -e로 넘길 명령. app_elf가 없으면 부트로더만 구운 보드를 띄운다.
    Renode는 @경로의 공백을 읽지 못하므로, isbc-renode 폴더에서 실행하고 스크립트는 상대경로로 부른다."""
    if app_elf:
        elf = Path(app_elf).as_posix()
        steps = [f'$global.appElf="{elf}"', 'include @isbc-board.resc']
    else:
        steps = ['include @isbc.resc', 'include @isbc-host.resc', 'logLevel 3']
    return '; '.join(steps + ['showAnalyzer sysbus.usart1', 'start'])


def port_in_use(host, port):
    # 명령 포트에 접속하면 펌웨어 클라이언트로 잡히므로, 배타적 bind로 비어 있는지만 확인한다.
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            exclusive = getattr(socket, 'SO_EXCLUSIVEADDRUSE', None)
            if exclusive is not None:
                probe.setsockopt(socket.SOL_SOCKET, exclusive, 1)
            probe.bind((host, port))
        return None
    except OSError as error:
        return error


class RenodeLauncher(QObject):
    finished = Signal(bool, str)

    def __init__(self, host, ports, parent=None):
        super().__init__(parent)
        self.host = host
        self.ports = dict(ports)

    def start(self, app_elf=None):
        if not RENODE.is_file():
            self.finished.emit(False, f'Renode를 찾을 수 없습니다: {RENODE}')
            return
        if app_elf:
            if not Path(app_elf).is_file():
                self.finished.emit(False, f'앱 ELF를 찾을 수 없습니다: {app_elf}')
                return
            if ';' in app_elf or '"' in app_elf:
                self.finished.emit(False, '앱 ELF 경로에 ; 또는 " 문자가 있으면 Renode에 넘길 수 없습니다.')
                return
        for port, label in self.ports.items():
            error = port_in_use(self.host, port)
            if error:
                self.finished.emit(False, f'{label} 포트({port})를 열 수 없습니다. 이미 다른 Renode가 실행 중인지 확인하세요. {error}')
                return

        started, _pid = QProcess.startDetached(str(RENODE), ['-e', renode_commands(app_elf)], str(ISBC_DIR))
        if started:
            what = Path(app_elf).name if app_elf else '부트로더만'
            self.finished.emit(True, f'Renode를 열었습니다({what}). 3D 보드 화면 연결을 기다리고 있습니다.')
        else:
            self.finished.emit(False, 'Renode를 열 수 없습니다.')

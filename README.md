# ISBC 보드 가상 테스트 (Renode)

ISBC 보드(회로도 V0.4)를 [Renode](https://renode.io) 1.17로 PC에서 가상 실행하는 도구입니다.
실기처럼 ISBC 부트로더가 먼저 돌고, 그 위에 올린 앱 펌웨어(STM32CubeIDE Debug 빌드 ELF)로 넘어갑니다.
버튼 하나로 Renode, USART1 콘솔, TCP 명령, 3D 보드 화면을 함께 띄웁니다.

## 바로 실행하기

1. [Releases](../../releases)에서 `ISBC_Renode_*.zip`을 받습니다. Renode와 Python(PySide6)이 들어 있어 설치가 필요 없습니다.
2. 압축을 **먼저 풀고**(zip 안에서 바로 실행하지 않기), 바탕화면·다운로드처럼 얕은 폴더에 둡니다.
3. `ISBC 보드 가상 테스트.bat`을 실행합니다.
4. 앱 ELF를 고르고 **Renode + UART 열기**를 누릅니다.

자세한 사용법은 [사용설명.txt](사용설명.txt)에 있습니다.

## 연결 정보

| 항목 | 주소 |
|---|---|
| USART1 (USB CLI) | Renode의 USART1 창 |
| TCP 명령 (펌웨어 8000) | `127.0.0.1:18000`, CRLF |
| 펌웨어 웹 (펌웨어 80) | `http://127.0.0.1:18080` |
| 3D 보드 화면 | `http://127.0.0.1:18100` |

## 가상 보드 구성

- **PCB_1**: STM32F746, ISBC 부트로더, W5300 이더넷, AT24C256 EEPROM, LED0~3, DIP PSW0~3(OFF), SD 감지(카드 없음), USART1/USART2/UART4
- **PCB_2**: TMC429, TMC2660 x2, MCP23S17 x2 (입력 16점, 출력 16점)
- **아직 모델 없음**: AD7124 온도, SD 카드 읽기/쓰기, CAN, RS485 통신, ADC, PWM

## 폴더 구성

```
ISBC 보드 가상 테스트.bat   실행
apps/                       예시 앱 ELF
isbc-renode/                가상 보드 (Renode 스크립트, C# 모델, 부트로더, 3D 화면, 창 프로그램)
  isbc.resc                 보드 + 부트로더
  isbc-app.resc             앱 ELF 굽기 ($global.appElf)
  isbc-host.resc            TCP·3D 화면 포트
  isbc-board.resc           위 세 단계를 한 번에
  isbc-usart.resc           + USART1 창
  simulator/                PySide6 창 (main.py)
renode_1.17.0-portable/     Renode (git에는 없음, Releases zip에 포함)
python/                     Python 3.14 + PySide6 6.11 (git에는 없음, Releases zip에 포함)
```

## 소스에서 실행하기

git으로 받은 경우 런타임이 없으므로 아래 둘을 같은 폴더에 넣습니다.

- `renode_1.17.0-portable/` : Renode 1.17.0 Windows portable
- `python/` : Python 3.14 + `pip install PySide6==6.11.2` (또는 `python\pythonw.exe` 대신 설치된 Python으로 `isbc-renode\simulator\main.py` 실행)

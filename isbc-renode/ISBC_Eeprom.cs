using System;
using System.IO;

using Antmicro.Renode.Exceptions;

namespace Antmicro.Renode.Peripherals.I2C
{
    // AT24C256C storage used by the ISBC app. Writes complete immediately in the emulator.
    // BackingFile를 지정하면 EEPROM 내용이 파일에 저장되어 Renode를 다시 켜도 유지된다(실기 EEPROM과 같은 동작).
    public sealed class ISBC_Eeprom : II2CPeripheral
    {
        public ISBC_Eeprom()
        {
            Array.Fill(storage, (byte)0xFF);
        }

        public void BackingFile(string path)
        {
            backingPath = path;
            if(!File.Exists(path))
            {
                Save();
                return;
            }
            var data = File.ReadAllBytes(path);
            if(data.Length != storage.Length)
            {
                throw new RecoverableException($"EEPROM 파일 크기가 {storage.Length}바이트가 아닙니다: {path}");
            }
            Array.Copy(data, storage, storage.Length);
        }

        public void Erase()
        {
            Array.Fill(storage, (byte)0xFF);
            Save();
        }

        public void Write(byte[] data)
        {
            if(data.Length < 2)
            {
                return;
            }

            address = ((data[0] << 8) | data[1]) % storage.Length;
            var pageStart = address - address % PageSize;
            for(var index = 2; index < data.Length; index++)
            {
                storage[address] = data[index];
                address = pageStart + (address + 1) % PageSize;
                dirty = true;
            }
        }

        public byte[] Read(int count = 1)
        {
            var result = new byte[count];
            for(var index = 0; index < count; index++)
            {
                result[index] = storage[address];
                address = (address + 1) % storage.Length;
            }
            return result;
        }

        public void FinishTransmission()
        {
            // 실기 EEPROM은 STOP 조건에서 페이지 쓰기를 확정한다. 같은 시점에 파일로 저장한다.
            if(dirty)
            {
                Save();
            }
        }

        public void Reset()
        {
            address = 0;
        }

        private void Save()
        {
            dirty = false;
            if(backingPath != null)
            {
                File.WriteAllBytes(backingPath, storage);
            }
        }

        private int address;
        private bool dirty;
        private string backingPath;
        private readonly byte[] storage = new byte[32 * 1024];
        private const int PageSize = 64;
    }
}

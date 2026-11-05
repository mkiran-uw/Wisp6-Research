#include "wisp-base.h"
#include "Camera/HM.h"
#include "camera_protocol.h"

WISP_dataStructInterface_t wispData;

#pragma DATA_SECTION(image, ".camera_frame")
uint8_t image[CAMERA_IMAGE_SIZE];

static camera_session_t session;
static volatile uint8_t command_pending;
static volatile uint8_t command_bank;
static volatile uint8_t command_address;
static volatile uint16_t command_value;
static uint16_t block_write_buffer[6];

static void write_callback(void)
{
    command_bank = RWData.memBank;
    command_address = RWData.wordPtr;
    command_value = RWData.wrData;
    command_pending = 1;
}

void main(void)
{
    uint16_t command;
    camera_error_t error;

    WISP_init();
    P4DIR |= BIT3;
    P4OUT |= BIT3;
    RWData.bwrBufPtr = block_write_buffer;
    WISP_getDataBuffers(&wispData);
    WISP_registerCallback_WRITE(&write_callback);
    WISP_setMode(MODE_READ | MODE_WRITE | MODE_USES_SEL);
    WISP_setAbortConditions(CMD_ID_ACK | CMD_ID_READ | CMD_ID_WRITE);

    camera_session_init(&session);
    wispData.epcBuf[0] = CAMERA_TAG_ID;
    wispData.epcBuf[1] = CAMERA_PROTOCOL;
    wispData.epcBuf[9] = 0x60;
    wispData.epcBuf[10] = *((uint8_t *)INFO_WISP_TAGID + 1);
    wispData.epcBuf[11] = *((uint8_t *)INFO_WISP_TAGID);
    P3OUT &= ~BIT3;
    P3DIR |= BIT3;
    camera_window(&session, image, wispData.readBufPtr);

    while (FOREVER)
    {
        WISP_doRFID();
        if (command_pending)
        {
            __disable_interrupt();
            command = command_value;
            command_pending = 0;
            if (command_bank != 3 || command_address != 0)
            {
                session.state = CAMERA_ERROR;
                session.error = CAMERA_BAD_BANK;
                session.selected_chunk = CAMERA_STATUS_CHUNK;
            }
            else if (camera_command(&session, command))
            {
                camera_window(&session, image, wispData.readBufPtr);
                error = camera_capture(image, (uint8_t)((command & CAMERA_TEST_PATTERN) != 0));
                camera_complete(&session, error, image);
            }
            camera_window(&session, image, wispData.readBufPtr);
        }
    }
}

/**
 * @file       usr.c
 * @brief      WISP application-specific code set
 * @details    The WISP application developer's implementation goes here.
 *
 * @author     Aaron Parks, UW Sensor Systems Lab
 *
 */

#include "wisp-base.h"

WISP_dataStructInterface_t wispData;

void simpleDelay(volatile uint32_t count)
{
    while(count--)
    {
        __no_operation();
    }
}

void ledBlinks(uint8_t count, uint32_t duration)
{
    while(count--)
    {
        BITSET(PLEDOUT, PIN_LED);

        simpleDelay(5000);

        BITCLR(PLEDOUT, PIN_LED);

        simpleDelay(duration);
    }
}
/**
 * Called after successful ACK reply
 */
void my_ackCallback(void)
{
    // Quick blink when ACK received
    ledBlinks(1, 1000);
}

/**
 * Called after successful READ command
 */
void my_readCallback(void)
{
    // Double blink for READ
    ledBlinks(2, 1000);
}

/**
 * Called after successful WRITE command
 */
void my_writeCallback(void)
{
    // Triple blink for WRITE
    ledBlinks(3, 1000);
}

/**
 * Called after successful BLOCKWRITE command
 */
void my_blockWriteCallback(void)
{
    // Fast blinking for BLOCKWRITE
    ledBlinks(4, 500);
}

/**
 * Main application
 */
void main(void)
{
    WISP_init();

    // Configure LED pin
    P4DIR |= BIT3;

    // Turn LED OFF initially
    P4OUT &= ~BIT3;

    // Register callbacks
    WISP_registerCallback_ACK(&my_ackCallback);
    WISP_registerCallback_READ(&my_readCallback);
    WISP_registerCallback_WRITE(&my_writeCallback);
    WISP_registerCallback_BLOCKWRITE(&my_blockWriteCallback);

    // Initialize BlockWrite buffer
    uint16_t bwr_array[6] = {0};
    RWData.bwrBufPtr = bwr_array;

    // Get EPC/READ/WRITE buffers
    WISP_getDataBuffers(&wispData);

    // Configure RFID behavior
    WISP_setMode(MODE_READ | MODE_WRITE | MODE_USES_SEL);

    WISP_setAbortConditions(
        CMD_ID_READ |
        CMD_ID_WRITE |
        CMD_ID_ACK
    );

    // Configure EPC
    wispData.epcBuf[0]  = 0x00;
    wispData.epcBuf[1]  = 0x00;
    wispData.epcBuf[2]  = 0x00;
    wispData.epcBuf[3]  = 0x00;
    wispData.epcBuf[4]  = 0x00;
    wispData.epcBuf[5]  = 0x00;
    wispData.epcBuf[6]  = 0x00;
    wispData.epcBuf[7]  = 0x00;
    wispData.epcBuf[8]  = 0x00;
    wispData.epcBuf[9]  = 0x60;

    // Unique WISP ID from INFO memory
    wispData.epcBuf[10] =
        *((uint8_t*)INFO_WISP_TAGID + 1);

    wispData.epcBuf[11] =
        *((uint8_t*)INFO_WISP_TAGID);

    // Main loop
    while(FOREVER)
    {
        // Slow heartbeat blink
        ledBlinks(1, 20000);

        // Process RFID commands
        WISP_doRFID();
    }
}
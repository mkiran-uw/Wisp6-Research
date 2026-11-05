/*
 * HM01B0 camera integration, based on Ali Saffari's WISP6 driver.
 * Register settings and serial pixel wiring are retained from that driver.
 */
#include <msp430.h>
#include "HM.h"
#include "HMRegs.h"

#define SLAVE_ADDR 0x24
#define I2C_TIMEOUT_TICKS 50000U
#define CAPTURE_TIMEOUT_MS 2000U

typedef struct {
    uint16_t address;
    uint8_t value;
} camera_register_t;

static const camera_register_t settings[] = {
    {REG_QVGA_WIN_EN, 0x01},
    {REG_BIN_RDOUT_X, 0x03}, {REG_BIN_RDOUT_Y, 0x03}, {REG_BINNING_MODE, 0x03},
    {0x3044, 0x0A}, {0x3045, 0x00}, {0x3047, 0x0A},
    {0x3050, 0xC0}, {0x3051, 0x42}, {0x3053, 0x00}, {0x3054, 0x03},
    {0x3055, 0xF7}, {0x3056, 0xF8}, {0x3057, 0x29}, {0x3058, 0x1F},
    {0x3064, 0x00}, {0x3065, 0x04},
    {0x1000, 0x43}, {0x1001, 0x40}, {0x1002, 0x32}, {0x1003, 0x08},
    {0x1006, 0x01}, {0x1007, 0x08}, {0x0350, 0x7F},
    {0x1008, 0x00}, {0x1009, 0xA0}, {0x100A, 0x60}, {0x100B, 0x90}, {0x100C, 0x40},
    {REG_VSYNC_HSYNC_PIXEL_SHIFT_EN, 0x00},
    {0x2000, 0x07}, {0x2003, 0x00}, {0x2004, 0x1C},
    {0x2007, 0x00}, {0x2008, 0x58}, {0x200B, 0x00}, {0x200C, 0x7A},
    {0x200F, 0x00}, {0x2010, 0xB8}, {0x2013, 0x00}, {0x2014, 0x58},
    {0x2017, 0x00}, {0x2018, 0x9B},
    {0x2100, 0x01}, {0x2101, 0x15}, {0x2102, 0x01}, {0x2104, 0x07},
    {0x2105, 0x02}, {0x2106, 0xA4}, {0x2108, 0x33}, {0x210A, 0x00},
    {0x210B, 0x80}, {0x210F, 0x00}, {0x2110, 0xE9}, {0x2111, 0x01},
    {0x2112, 0x17}, {0x2150, 0x03},
    {0x0205, 0x05}, {0x020E, 0x01}, {0x020F, 0x00}, {0x0202, 0x01}, {0x0203, 0x08},
    {REG_FRAME_LENGTH_LINES_H, 0x00}, {REG_FRAME_LENGTH_LINES_L, 0xCC},
    {REG_FRAME_LENGTH_PCK_H, 0x00}, {REG_FRAME_LENGTH_PCK_L, 0xEE},
    /* Gated pixel clock: no SPI clocks during line/frame blanking. */
    {REG_OSC_CLK_DIV, 0x38}, {REG_BIT_CONTROL, 0x20}
};

static volatile uint8_t capture_done;

static camera_error_t wait_for(volatile uint16_t *reg, uint16_t mask, uint8_t set)
{
    uint16_t start = TA2R;
    for (;;)
    {
        if (UCB0IFG & UCNACKIFG)
            return CAMERA_I2C_NACK;
        if (UCB0IFG & UCALIFG)
            return CAMERA_I2C_ARBITRATION;
        if (((*reg & mask) != 0) == set)
            return CAMERA_OK;
        if ((uint16_t)(TA2R - start) >= I2C_TIMEOUT_TICKS)
            return CAMERA_I2C_TIMEOUT;
    }
}

static camera_error_t send_byte(uint8_t value)
{
    camera_error_t error = wait_for(&UCB0IFG, UCTXIFG0, 1);
    if (error == CAMERA_OK)
        UCB0TXBUF = value;
    return error;
}

static camera_error_t register_address(uint16_t address)
{
    camera_error_t error;
    UCB0IFG &= ~(UCNACKIFG | UCALIFG);
    error = wait_for(&UCB0CTLW0, UCTXSTP, 0);
    if (error != CAMERA_OK)
        return error;
    UCB0CTLW0 |= UCTR | UCTXSTT;
    error = send_byte((uint8_t)(address >> 8));
    if (error == CAMERA_OK)
        error = send_byte((uint8_t)address);
    return error;
}

static camera_error_t write_register(uint16_t address, uint8_t value)
{
    camera_error_t error = register_address(address);
    if (error == CAMERA_OK)
        error = send_byte(value);
    if (error == CAMERA_OK)
        error = wait_for(&UCB0IFG, UCTXIFG0, 1);
    if (error == CAMERA_OK)
    {
        UCB0CTLW0 |= UCTXSTP;
        error = wait_for(&UCB0CTLW0, UCTXSTP, 0);
    }
    return error;
}

static camera_error_t read_register(uint16_t address, uint8_t *value)
{
    camera_error_t error = register_address(address);
    if (error == CAMERA_OK)
        error = wait_for(&UCB0IFG, UCTXIFG0, 1);
    if (error != CAMERA_OK)
        return error;
    UCB0CTLW0 &= ~UCTR;
    UCB0CTLW0 |= UCTXSTT;
    error = wait_for(&UCB0CTLW0, UCTXSTT, 0);
    if (error != CAMERA_OK)
        return error;
    UCB0CTLW0 |= UCTXSTP;
    error = wait_for(&UCB0IFG, UCRXIFG0, 1);
    if (error == CAMERA_OK)
    {
        *value = UCB0RXBUF;
        error = wait_for(&UCB0CTLW0, UCTXSTP, 0);
    }
    return error;
}

static void stop_camera(void)
{
    P3IE &= ~BIT6;
    P3IFG &= ~BIT6;
    DMA0CTL = 0;
    UCA0IE = 0;
    UCA0CTLW0 |= UCSWRST;
    UCB0IE = 0;
    UCB0CTLW0 |= UCSWRST;
    P3OUT &= ~BIT3;
    PJSEL0 &= ~BIT0;
    PJSEL1 &= ~BIT0;
    PJOUT &= ~BIT0;
    TA2CTL = MC__STOP;
}

camera_error_t camera_capture(uint8_t *image, uint8_t test_pattern)
{
    camera_error_t error = CAMERA_OK;
    uint16_t i;
    uint16_t saved_gie = __get_SR_register() & GIE;
    uint8_t saved_rx_ie = P2IE & BIT3;
    uint8_t id;

    __disable_interrupt();
    P2IE &= ~BIT3;
    P2IFG &= ~BIT3;
    stop_camera();
    capture_done = 0;
    FRCTL0 = FRCTLPW | NWAITS_1;
    CSCTL0_H = 0xA5;
    CSCTL1 = DCORSEL | DCOFSEL_4;
    CSCTL2 = SELA__VLOCLK | SELM__DCOCLK | SELS__DCOCLK;
    CSCTL3 = DIVA_0 | DIVS_2 | DIVM_0;
    CSCTL0_H = 0;
    TA2EX0 = 0;
    TA2CTL = TASSEL__SMCLK | ID__8 | MC__CONTINUOUS | TACLR;

    P3DIR |= BIT3;
    P3OUT |= BIT3;
    PJDIR |= BIT0;
    PJSEL1 &= ~BIT0;
    PJSEL0 |= BIT0;
    __delay_cycles(160000);
    P1SEL0 &= ~(BIT6 | BIT7);
    P1SEL1 |= BIT6 | BIT7;
    UCB0CTLW0 = UCSWRST | UCMODE_3 | UCMST | UCSSEL__SMCLK | UCSYNC;
    UCB0BRW = 10;
    UCB0I2CSA = SLAVE_ADDR;
    UCB0IFG = 0;
    UCB0CTLW0 &= ~UCSWRST;

    error = write_register(REG_SW_RESET, 0x01);
    if (error == CAMERA_OK)
    {
        __delay_cycles(160000);
        error = read_register(REG_MODEL_ID_H, &id);
        if (error == CAMERA_OK && id != 0x01)
            error = CAMERA_WRONG_ID;
    }
    if (error == CAMERA_OK)
    {
        error = read_register(REG_MODEL_ID_L, &id);
        if (error == CAMERA_OK && id != 0xB0)
            error = CAMERA_WRONG_ID;
    }
    if (error == CAMERA_OK)
        error = write_register(REG_MODE_SELECT, 0x00);
    if (error == CAMERA_OK)
        error = write_register(REG_ANA_REGISTER_17, 0x00);
    for (i = 0; error == CAMERA_OK && i < sizeof(settings) / sizeof(settings[0]); i++)
        error = write_register(settings[i].address, settings[i].value);
    if (error == CAMERA_OK)
        error = write_register(REG_TEST_PATTERN_MODE, test_pattern ? TEST_PATTERN_WALKING_1 : 0);

    if (error == CAMERA_OK)
    {
        P1SEL0 &= ~BIT5;
        P1SEL1 |= BIT5;
        P2SEL0 &= ~(BIT0 | BIT1);
        P2SEL1 |= BIT0 | BIT1;
        UCA0CTLW0 = UCSWRST | UCSYNC | UCMSB;
        UCA0IE = 0;
        UCA0CTLW0 &= ~UCSWRST;
        DMACTL0 = (DMACTL0 & ~DMA0TSEL_31) | DMA0TSEL_14;
        DMA0CTL = DMADT_0 | DMADSTINCR_3 | DMADSTBYTE | DMASRCBYTE | DMALEVEL;
        DMA0SZ = CAMERA_IMAGE_SIZE;
        __data20_write_long((unsigned long)&DMA0SA, (unsigned long)&UCA0RXBUF);
        __data20_write_long((unsigned long)&DMA0DA, (unsigned long)image);
        P3DIR &= ~BIT6;
        P3SEL0 &= ~BIT6;
        P3SEL1 &= ~BIT6;
        P3REN &= ~BIT6;
        P3IES |= BIT6;
        error = write_register(REG_MODE_SELECT, MODE_STREAMING);
    }
    if (error == CAMERA_OK)
    {
        P3IFG &= ~BIT6;
        P3IE |= BIT6;
        __enable_interrupt();
        for (i = 0; !capture_done && i < CAPTURE_TIMEOUT_MS; i++)
            __delay_cycles(16000);
        __disable_interrupt();
        if (!capture_done)
            error = CAMERA_FRAME_TIMEOUT;
    }
    stop_camera();
    P2IFG &= ~BIT3;
    P2IE |= saved_rx_ie;
    if (saved_gie)
        __enable_interrupt();
    return error;
}

#pragma vector=PORT3_VECTOR
__interrupt void Port_3(void)
{
    if (P3IFG & BIT6)
    {
        P3IE &= ~BIT6;
        P3IFG &= ~BIT6;
        /* Restart byte alignment at the end of a frame, before the next pixels. */
        UCA0CTLW0 |= UCSWRST;
        UCA0IFG &= ~UCRXIFG;
        UCA0CTLW0 &= ~UCSWRST;
        DMA0CTL |= DMAEN | DMAIE;
    }
}

#pragma vector=DMA_VECTOR
__interrupt void DMA(void)
{
    if (DMA0CTL & DMAIFG)
    {
        DMA0CTL &= ~(DMAIFG | DMAEN | DMAIE);
        P3OUT &= ~BIT3;
        capture_done = 1;
    }
}
